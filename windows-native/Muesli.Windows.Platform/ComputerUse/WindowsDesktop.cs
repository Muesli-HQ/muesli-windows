using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Muesli.Windows.ComputerUse;

/// <summary>
/// Windows implementation of the Computer Use desktop: resolves the target window, observes it,
/// and executes desktop tools with UI Automation and synthetic input. It works in the background
/// where it can: windows are read and drawn without coming forward, and elements are pressed,
/// set and scrolled through UI Automation. Only keyboard input and coordinate clicks bring the
/// target to the front; the user's pointer is put back after each step and their window after
/// the run. Apps on the safety deny-list are refused.
/// </summary>
public sealed class WindowsDesktop : ICuaDesktop
{
    private readonly UiaObserver _observer = new();
    private readonly long _userWindow = Win32Desktop.ForegroundWindow();
    private readonly HashSet<long> _touched = [];
    private readonly HashSet<long> _hidden = [];
    private bool _launched;
    private bool _broughtForward;
    private CuaWindow? _target;
    private IReadOnlyList<(string Name, string Id)>? _startApps;

    public CuaWindow? Target => _target;

    public CuaWindowState Observe(string? app, long? windowId, bool includeScreenshot)
    {
        var window = Resolve(app, windowId);
        if (window is null)
            return CuaWindowState.Empty(app is null ? "No window is open" : $"No open window for {app}");
        if (CuaSafety.BlockReason(window.AppId, window.Title) is { } reason)
            return CuaWindowState.Empty(window.AppName) with { AppId = window.AppId, WindowId = window.Id, VisibleText = reason };
        _target = window;
        _touched.Add(window.Id);
        if (Win32Desktop.IsMinimized(window.Id)) _hidden.Add(window.Id);
        Wake();
        try { return _observer.Observe(window, includeScreenshot); }
        finally { Sleep(); }
    }

    /// <summary>
    /// A window the user minimized is restored only for the moment it is read or acted on, and
    /// invisibly (ghosted): the user never sees it, and between steps it is a normal minimized
    /// window they can open themselves. Restoring happens under the foreground lock because
    /// Chrome activates itself when restored.
    /// </summary>
    private void Wake()
    {
        if (_target is not { } window || !_hidden.Contains(window.Id) || !Win32Desktop.IsMinimized(window.Id)) return;
        if (!Win32Desktop.Ghost(window.Id)) { _hidden.Remove(window.Id); return; } // Already layered: shown normally.
        Win32Desktop.LockForeground(true);
        Win32Desktop.ShowWithoutActivating(window.Id);
        Thread.Sleep(250); // Let it repaint before it is read.
        Win32Desktop.LockForeground(false);
    }

    private void Sleep()
    {
        foreach (var window in _hidden.Where(Win32Desktop.IsAlive))
        {
            if (!Win32Desktop.IsMinimized(window)) Win32Desktop.MinimizeWithoutActivating(window);
            Win32Desktop.Unghost(window);
        }
    }

    public CuaResult Execute(CuaToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pointer = Win32Desktop.CursorPosition();
        var foreground = Win32Desktop.ForegroundWindow();
        _broughtForward = false;
        Win32Desktop.LockForeground(true);
        try
        {
            Wake();
            return call.Name switch
            {
                "list_apps" => ListApps(),
                "list_windows" => ListWindows(call.App),
                "launch_app" => LaunchApp(call, cancellationToken),
                "activate_window" => ActivateWindow(call),
                "click" or "click_element" when call.ElementIndex is not null => ClickElement(call),
                "click" or "click_point" => ClickPoint(call),
                "move_cursor" => MoveCursor(call),
                "perform_secondary_action" => SecondaryAction(call),
                "set_value" => SetValue(call),
                "type_text" => TypeText(call, cancellationToken),
                "paste_text" => PasteText(call),
                "press_key" or "hotkey" => PressKey(call),
                "scroll" => Scroll(call),
                "drag" => Drag(call),
                "open_url" => OpenUrl(call, cancellationToken),
                "page_get_text" => PageText(call),
                "list_browser_tabs" => ListTabs(call),
                "activate_browser_tab" => ActivateTab(call),
                _ => new CuaResult(CuaResultStatus.Unsupported, $"{call.Name} is not a desktop tool."),
            };
        }
        catch (ArgumentException exception) { return CuaResult.Fail(exception.Message); }
        catch (InvalidOperationException exception) { return CuaResult.Fail(exception.Message); }
        catch (Exception exception) when (exception is FlaUI.Core.Exceptions.ElementNotAvailableException
                                              or System.Runtime.InteropServices.COMException)
        {
            return CuaResult.Fail("The element is no longer available; call get_app_state for a fresh state.");
        }
        finally
        {
            Sleep();
            Win32Desktop.LockForeground(false);
            // The pointer goes back where the user left it; move_cursor exists to leave it on a hover target.
            if (call.Name != "move_cursor" && Win32Desktop.CursorPosition() != pointer) Win32Desktop.SetCursor(pointer);
            // Chrome raises its own window when a link or tab is pressed through UI Automation; a
            // background step gives the foreground straight back.
            var now = Win32Desktop.ForegroundWindow();
            if (!_broughtForward && now != foreground && _touched.Contains(now) && Win32Desktop.IsAlive(foreground))
                Win32Desktop.Activate(foreground);
        }
    }

    // ---- Apps and windows ----

    private CuaResult ListApps()
    {
        var running = Win32Desktop.ListWindows()
            .Select(window => $"- {window.AppName} (app_id {window.AppId}, window_id {window.Id}): \"{window.Title}\"");
        var installed = StartApps().Select(app => app.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return CuaResult.Ok($"Open windows:\n{string.Join("\n", running)}\n\nInstalled apps (launch_app by name): {Truncate(string.Join(", ", installed), 6000)}");
    }

    private static CuaResult ListWindows(string? app)
    {
        var windows = Win32Desktop.ListWindows().Where(window => app is null || Matches(window, app) > 0).ToList();
        return CuaResult.Ok(windows.Count == 0 ? "No matching windows are open."
            : string.Join("\n", windows.Select(window =>
                $"- window_id {window.Id}: {window.AppName} (app_id {window.AppId}) \"{window.Title}\"{(window.IsForeground ? " [foreground]" : "")}")));
    }

    private CuaResult ActivateWindow(CuaToolCall call)
    {
        var window = Resolve(call.App, call.Number("window_id") is { } id ? (long)id : null)
                     ?? throw new InvalidOperationException("No matching window is open.");
        return Focus(window) is { } failure ? failure : CuaResult.Ok($"Activated {window.AppName}: \"{window.Title}\".");
    }

    private CuaResult LaunchApp(CuaToolCall call, CancellationToken cancellationToken)
    {
        var app = call.App ?? throw new ArgumentException("launch_app needs app_name or app_id.");
        _launched = true;
        if (BestWindow(app) is { } existing)
            return Focus(existing) is { } failure ? failure : CuaResult.Ok($"{existing.AppName} was already open and is now in front: \"{existing.Title}\".");

        var before = Win32Desktop.ListWindows().Select(window => window.Id).ToHashSet();
        string launched;
        if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(app))
        {
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
            launched = Path.GetFileNameWithoutExtension(app);
        }
        else
        {
            var match = MatchStartApp(app) ?? throw new InvalidOperationException(
                $"No installed app named \"{app}\". Use list_apps to see installed app names.");
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{match.Id}") { UseShellExecute = true });
            launched = match.Name;
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(300);
            var windows = Win32Desktop.ListWindows();
            var window = windows.Where(candidate => !before.Contains(candidate.Id))
                             .OrderByDescending(candidate => Matches(candidate, launched)).FirstOrDefault()
                         ?? BestWindow(launched);
            if (window is null) continue;
            Thread.Sleep(400); // Let the first frame render before it is observed.
            return Focus(window) is { } failure ? failure : CuaResult.Ok($"Launched {launched}: \"{window.Title}\".");
        }
        return CuaResult.Fail($"Started {launched}, but no window appeared within 15 seconds.");
    }

    private IReadOnlyList<(string Name, string Id)> StartApps() => _startApps ??= Sta.Run(() =>
    {
        var apps = new List<(string, string)>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        foreach (var item in shell.NameSpace("shell:AppsFolder").Items())
            apps.Add(((string)item.Name, (string)item.Path));
        return (IReadOnlyList<(string, string)>)apps;
    });

    private (string Name, string Id)? MatchStartApp(string query)
    {
        var apps = StartApps();
        foreach (var predicate in new Func<string, bool>[]
                 {
                     name => name.Equals(query, StringComparison.OrdinalIgnoreCase),
                     name => name.StartsWith(query, StringComparison.OrdinalIgnoreCase),
                     name => name.Contains(query, StringComparison.OrdinalIgnoreCase),
                 })
        {
            var found = apps.Where(app => predicate(app.Name)).OrderBy(app => app.Name.Length).ToList();
            if (found.Count > 0) return found[0];
        }
        return null;
    }

    private CuaWindow? Resolve(string? app, long? windowId)
    {
        if (windowId is { } id and > 0) return Win32Desktop.Describe(new IntPtr(id)); // Models send 0 for "none".
        if (app is not null) return BestWindow(app);
        if (_target is { } target && Win32Desktop.IsAlive(target.Id)) return Win32Desktop.Describe(new IntPtr(target.Id)) ?? target;
        return Win32Desktop.UserWindow();
    }

    private static CuaWindow? BestWindow(string app) =>
        Win32Desktop.ListWindows().Select(window => (window, score: Matches(window, app)))
            .Where(match => match.score > 0).OrderByDescending(match => match.score).ThenByDescending(match => match.window.IsForeground)
            .Select(match => match.window).FirstOrDefault();

    private static int Matches(CuaWindow window, string app)
    {
        var query = app.Trim();
        if (query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) query = Path.GetFileNameWithoutExtension(query);
        if (window.AppId.Equals(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (window.AppName.Equals(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (window.Title.EndsWith(" - " + query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (window.AppName.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (query.Length >= 4 && window.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    /// <summary>Makes a window the target and brings it to the front; null on success.</summary>
    private CuaResult? Focus(CuaWindow window)
    {
        if (CuaSafety.BlockReason(window.AppId, window.Title) is { } reason) return CuaResult.Fail(reason);
        _target = window;
        _touched.Add(window.Id);
        _broughtForward = true;
        Win32Desktop.LockForeground(false);
        // Keyboard input must never go to a window the user cannot see.
        Win32Desktop.Unghost(window.Id);
        _hidden.Remove(window.Id);
        return Win32Desktop.Activate(window.Id) ? null : CuaResult.Fail($"Windows did not let Muesli bring {window.AppName} to the front.");
    }

    /// <summary>The window a tool acts on (window_id, the named app, else the current target),
    /// left where it is.</summary>
    private CuaWindow TargetWindow(CuaToolCall call)
    {
        var window = (call.Number("window_id") is { } id and > 0 ? Win32Desktop.Describe(new IntPtr((long)id)) : null)
                     ?? (call.App is { } app ? BestWindow(app) : null) ?? _target
                     ?? throw new InvalidOperationException("No target window yet. Call get_app_state or launch_app first.");
        if (!Win32Desktop.IsAlive(window.Id)) throw new InvalidOperationException($"{window.AppName}'s window has closed.");
        if (CuaSafety.BlockReason(window.AppId, window.Title) is { } reason) throw new InvalidOperationException(reason);
        _target = window;
        _touched.Add(window.Id);
        return window;
    }

    /// <summary>The target window, brought to the front for mouse or keyboard input.</summary>
    private CuaWindow PrepareInput(CuaToolCall call)
    {
        var window = TargetWindow(call);
        if (Focus(window) is { } failure) throw new InvalidOperationException(failure.Message);
        return window;
    }

    // ---- Elements and coordinates ----

    private (AutomationElement Element, CuaElement Info) Element(CuaToolCall call)
    {
        var index = call.ElementIndex ?? throw new ArgumentException($"{call.Name} needs element_index or element_id.");
        var state = _observer.Latest;
        if (state is null || _target is null || state.WindowId != _target.Id || _observer.Element(index) is not { } element)
            throw new ArgumentException($"Element e{index} is not in the latest state. Call get_app_state and use a current element.");
        return (element, state.Elements[index - 1]);
    }

    private static (double X, double Y) Center(AutomationElement element, CuaElement info)
    {
        try
        {
            var bounds = element.BoundingRectangle;
            if (bounds.Width > 0 && bounds.Height > 0) return (bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
        }
        catch (Exception exception) when (exception is FlaUI.Core.Exceptions.PropertyNotSupportedException
                                              or System.Runtime.InteropServices.COMException) { }
        return info.Frame is { } frame ? (frame.CenterX, frame.CenterY)
            : throw new InvalidOperationException("The element has no position on screen.");
    }

    /// <summary>Converts screenshot pixel coordinates to screen pixels using the latest screenshot.</summary>
    private (double X, double Y) ScreenPoint(CuaToolCall call, string xKey = "x", string yKey = "y")
    {
        var x = call.Number(xKey) ?? throw new ArgumentException($"{call.Name} needs {xKey}.");
        var y = call.Number(yKey) ?? throw new ArgumentException($"{call.Name} needs {yKey}.");
        var shot = _observer.Latest?.Screenshot ?? throw new ArgumentException("No screenshot yet; call get_app_state first.");
        if (call.String("screenshot_id") is { } id && id != shot.ScreenshotId)
            throw new ArgumentException("That screenshot_id is stale. Use coordinates from the latest screenshot.");
        if (x < 0 || y < 0 || x > shot.Width || y > shot.Height)
            throw new ArgumentException($"({x}, {y}) is outside the {shot.Width}x{shot.Height} screenshot.");
        return (shot.WindowFrame.X + x / shot.ScaleX, shot.WindowFrame.Y + y / shot.ScaleY);
    }

    /// <summary>A single left click presses the element through UI Automation (like macOS AXPress),
    /// which leaves the user's pointer and foreground alone; anything else, or an element with
    /// nothing to press, is a real click on the window brought to the front.</summary>
    private CuaResult ClickElement(CuaToolCall call)
    {
        var window = TargetWindow(call);
        var (element, info) = Element(call);
        var button = call.String("button") ?? "left";
        var clicks = call.Integer("clicks") ?? 1;
        var label = Describe(info);
        if (button == "left" && clicks == 1 && Press(element, label) is { } pressed) return pressed;
        if (Focus(window) is { } failure) return failure;
        var (x, y) = Center(element, info);
        if (Win32Desktop.RootWindowAt(x, y) != window.Id)
            return CuaResult.Fail($"{label} is covered by another window or off screen. Scroll it into view or close what covers it.");
        WindowsInput.Click(x, y, button, clicks);
        return CuaResult.Ok($"Clicked {label}.");
    }

    /// <summary>Invokes, toggles, selects or expands the element; null when it supports none of
    /// these or the app refused, so the caller clicks instead.</summary>
    private static CuaResult? Press(AutomationElement element, string label)
    {
        Func<string>? press = null;
        if (element.Patterns.Invoke.TryGetPattern(out var invoke)) press = () => { invoke.Invoke(); return $"Pressed {label}."; };
        else if (element.Patterns.Toggle.TryGetPattern(out var toggle)) press = () => { toggle.Toggle(); return $"Toggled {label}."; };
        else if (element.Patterns.SelectionItem.TryGetPattern(out var selection)) press = () => { selection.Select(); return $"Selected {label}."; };
        else if (element.Patterns.ExpandCollapse.TryGetPattern(out var expand))
            press = () =>
            {
                if (expand.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Expanded) expand.Collapse(); else expand.Expand();
                return $"Expanded or collapsed {label}.";
            };
        if (press is null) return null;
        // A classic button that opens a modal dialog returns from Invoke only when the dialog closes.
        var task = Task.Run(press);
        try
        {
            return task.Wait(TimeSpan.FromSeconds(3)) ? CuaResult.Ok(task.Result)
                : CuaResult.Ok($"Pressed {label}; the app is still busy, probably showing a dialog.");
        }
        catch (AggregateException exception) when (exception.InnerException is FlaUI.Core.Exceptions.ElementNotAvailableException
                                                       or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private CuaResult ClickPoint(CuaToolCall call)
    {
        PrepareInput(call);
        var (x, y) = ScreenPoint(call);
        WindowsInput.Click(x, y, call.String("button") ?? "left", call.Integer("clicks") ?? 1);
        return CuaResult.Ok($"Clicked at ({call.Number("x")}, {call.Number("y")}){LabelSuffix(call)}.");
    }

    private CuaResult MoveCursor(CuaToolCall call)
    {
        PrepareInput(call);
        var (x, y) = ScreenPoint(call);
        WindowsInput.MoveTo(x, y);
        return CuaResult.Ok($"Moved the pointer{LabelSuffix(call)}.");
    }

    private CuaResult SecondaryAction(CuaToolCall call)
    {
        var window = TargetWindow(call);
        var (element, info) = Element(call);
        var action = (call.String("action_name") ?? call.String("action") ?? throw new ArgumentException("perform_secondary_action needs action_name."))
            .Replace("AX", "", StringComparison.Ordinal).Replace(" ", "_").ToLowerInvariant();
        var label = Describe(info);
        switch (action)
        {
            case "expand" when element.Patterns.ExpandCollapse.TryGetPattern(out var expand): expand.Expand(); break;
            case "collapse" when element.Patterns.ExpandCollapse.TryGetPattern(out var collapse): collapse.Collapse(); break;
            case "toggle" when element.Patterns.Toggle.TryGetPattern(out var toggle): toggle.Toggle(); break;
            case "select" when element.Patterns.SelectionItem.TryGetPattern(out var select): select.Select(); break;
            case "invoke" or "press" when element.Patterns.Invoke.TryGetPattern(out var invoke): invoke.Invoke(); break;
            case "focus" or "raise": element.Focus(); break;
            case "scroll_up" or "scroll_down" or "scrollup" or "scrolldown" or "scroll_down_by_page" or "scroll_up_by_page"
                when element.Patterns.Scroll.TryGetPattern(out var scroll):
                scroll.Scroll(ScrollAmount.NoAmount, action.Contains("up") ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement);
                break;
            case "show_menu" or "showmenu" or "context_menu":
                if (Focus(window) is { } failure) return failure;
                var (x, y) = Center(element, info);
                WindowsInput.Click(x, y, "right");
                break;
            case "increment" or "decrement" when element.Patterns.RangeValue.TryGetPattern(out var range):
                var change = action == "increment" ? range.SmallChange.ValueOrDefault : -range.SmallChange.ValueOrDefault;
                range.SetValue(Math.Clamp(range.Value.ValueOrDefault + change, range.Minimum.ValueOrDefault, range.Maximum.ValueOrDefault));
                break;
            default:
                return new CuaResult(CuaResultStatus.Unsupported, $"{label} does not support \"{action}\". Its actions are: {string.Join(", ", info.ActionNames)}.");
        }
        return CuaResult.Ok($"Performed {action} on {label}.");
    }

    private CuaResult SetValue(CuaToolCall call)
    {
        TargetWindow(call);
        var (element, info) = Element(call);
        var value = call.String("value") ?? (call.Arguments["value"] is null ? throw new ArgumentException("set_value needs value.") : "");
        if (info.Label.Contains("password", StringComparison.Ordinal)) return CuaResult.Fail("Muesli does not enter passwords.");
        if (element.Patterns.Value.TryGetPattern(out var pattern) && !pattern.IsReadOnly.ValueOrDefault)
        {
            pattern.SetValue(value);
            return CuaResult.Ok($"Set {Describe(info)} to \"{Truncate(value, 60)}\".");
        }
        if (element.Patterns.RangeValue.TryGetPattern(out var range) && double.TryParse(value, out var number))
        {
            range.SetValue(number);
            return CuaResult.Ok($"Set {Describe(info)} to {number}.");
        }
        return new CuaResult(CuaResultStatus.Unsupported, $"{Describe(info)} has no settable value. Click it and use type_text instead.");
    }

    private CuaResult TypeText(CuaToolCall call, CancellationToken cancellationToken)
    {
        var text = call.String("text") ?? throw new ArgumentException("type_text needs text.");
        FocusForText(call);
        WindowsInput.TypeText(text, cancellationToken);
        return CuaResult.Ok($"Typed {text.Length} characters{LabelSuffix(call)}.");
    }

    private CuaResult PasteText(CuaToolCall call)
    {
        var text = call.String("text") ?? throw new ArgumentException("paste_text needs text.");
        FocusForText(call);
        WindowsInput.PasteText(text);
        return CuaResult.Ok($"Pasted {text.Length} characters{LabelSuffix(call)}; the clipboard was restored.");
    }

    /// <summary>Brings the app forward and, when an element is named, focuses it (clicking edit
    /// surfaces so the caret lands inside).</summary>
    private void FocusForText(CuaToolCall call)
    {
        var window = PrepareInput(call);
        if (call.ElementIndex is null) return;
        var (element, info) = Element(call);
        if (info.Label.Contains("password", StringComparison.Ordinal)) throw new InvalidOperationException("Muesli does not type into password fields.");
        if (info.Label.Contains("focused", StringComparison.Ordinal)) return;
        var (x, y) = Center(element, info);
        if (Win32Desktop.RootWindowAt(x, y) == window.Id) WindowsInput.Click(x, y);
        else element.Focus();
        Thread.Sleep(80);
    }

    private CuaResult PressKey(CuaToolCall call)
    {
        var key = call.String("key") ?? throw new ArgumentException($"{call.Name} needs key.");
        var chord = WindowsInput.ParseChord(key, call.Strings("modifiers"));
        PrepareInput(call);
        WindowsInput.PressChord(chord);
        var modifiers = call.Strings("modifiers");
        return CuaResult.Ok($"Pressed {(modifiers.Count > 0 ? string.Join("+", modifiers) + "+" : "")}{key}.");
    }

    private CuaResult Scroll(CuaToolCall call)
    {
        var window = TargetWindow(call);
        var direction = (call.String("direction") ?? "down").ToLowerInvariant();
        var pages = call.Number("pages") ?? 1;
        var scrollable = call.ElementIndex is not null ? Element(call).Element : call.Number("x") is null ? LargestScrollable(window) : null;
        if (scrollable is not null && ScrollInBackground(scrollable, direction, pages))
            return CuaResult.Ok($"Scrolled {direction} {pages:0.#} page(s).");

        if (Focus(window) is { } failure) return failure;
        var notches = (int)Math.Max(1, Math.Round(pages * 5)) * (direction is "up" or "left" ? -1 : 1);
        double x, y;
        if (call.ElementIndex is not null) (x, y) = Center(Element(call).Element, Element(call).Info);
        else if (call.Number("x") is not null) (x, y) = ScreenPoint(call);
        else
        {
            var frame = Win32Desktop.WindowFrame(new IntPtr(window.Id)) ?? window.Frame;
            (x, y) = (frame.CenterX, frame.CenterY);
        }
        WindowsInput.Scroll(x, y, notches, direction is "left" or "right");
        return CuaResult.Ok($"Scrolled {direction} {pages:0.#} page(s).");
    }

    private CuaResult Drag(CuaToolCall call)
    {
        PrepareInput(call);
        var (fromX, fromY) = ScreenPoint(call);
        var (toX, toY) = ScreenPoint(call, "to_x", "to_y");
        WindowsInput.Drag(fromX, fromY, toX, toY);
        return CuaResult.Ok($"Dragged{LabelSuffix(call)}.");
    }

    /// <summary>The biggest scrollable element of the latest state: the page, list or document.</summary>
    private AutomationElement? LargestScrollable(CuaWindow window)
    {
        if (_observer.Latest is not { } state || state.WindowId != window.Id) return null;
        var best = state.Elements.Where(element => element.ActionNames.Contains("scroll_down") && element.Frame is not null)
            .MaxBy(element => element.Frame!.Width * element.Frame.Height);
        return best is null ? null : _observer.Element(best.ElementIndex);
    }

    private static bool ScrollInBackground(AutomationElement element, string direction, double pages)
    {
        try
        {
            if (!element.Patterns.Scroll.TryGetPattern(out var scroll)) return false;
            var horizontal = direction is "left" or "right";
            if (!(horizontal ? scroll.HorizontallyScrollable : scroll.VerticallyScrollable).ValueOrDefault) return false;
            var amount = direction is "up" or "left" ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement;
            for (var page = 0; page < Math.Max(1, (int)Math.Round(pages)); page++)
                scroll.Scroll(horizontal ? amount : ScrollAmount.NoAmount, horizontal ? ScrollAmount.NoAmount : amount);
            return true;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException
                                              or FlaUI.Core.Exceptions.PropertyNotSupportedException) { return false; }
    }

    // ---- Browser ----

    private const int MaxPageText = 12000;

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi" };

    /// <summary>Opens a web address in a new tab of the default browser, with no typing. The browser
    /// raises itself; the step's foreground restore puts the user's window back.</summary>
    private CuaResult OpenUrl(CuaToolCall call, CancellationToken cancellationToken)
    {
        var text = call.String("url") ?? throw new ArgumentException("open_url needs url.");
        if (!Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            return CuaResult.Fail("open_url only opens http or https web addresses.");
        var browsers = Win32Desktop.ListWindows().Where(window => Browsers.Contains(window.AppId)).ToList();
        var before = browsers.ToDictionary(window => window.Id, window => window.Title);
        // The browser restores a minimized window to show the tab; ghosted, it does so invisibly.
        foreach (var window in browsers.Where(window => Win32Desktop.IsMinimized(window.Id) && Win32Desktop.Ghost(window.Id)))
            _hidden.Add(window.Id);
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        return WaitForTab(url, before, cancellationToken);
    }

    private CuaResult WaitForTab(Uri url, Dictionary<long, string> before, CancellationToken cancellationToken)
    {
        // The tab lands in a new window, or retitles an existing one as it starts loading.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(250);
            var window = Win32Desktop.ListWindows().FirstOrDefault(window => Browsers.Contains(window.AppId)
                && (!before.TryGetValue(window.Id, out var title) || title != window.Title));
            if (window is null) continue;
            Thread.Sleep(400); // The browser raises itself just after the tab appears.
            _target = window;
            _touched.Add(window.Id);
            return CuaResult.Ok($"Opened {url.AbsoluteUri} in a new {window.AppName} tab (window_id {window.Id}).");
        }
        return CuaResult.Fail($"Asked Windows to open {url.AbsoluteUri}, but no browser tab appeared within 10 seconds.");
    }

    /// <summary>Reads the page through UI Automation: the whole document, not just what is on
    /// screen, and without bringing the browser forward.</summary>
    private CuaResult PageText(CuaToolCall call)
    {
        var window = TargetWindow(call);
        var page = Documents(window).MaxBy(Area)
                   ?? throw new InvalidOperationException($"{window.AppName} shows no web page or document.");
        var text = page.Patterns.Text.TryGetPattern(out var pattern)
            ? pattern.DocumentRange.GetText(MaxPageText)
            : string.Join("\n", page.FindAllDescendants(condition => condition.ByControlType(ControlType.Text))
                .Select(line => line.Properties.Name.ValueOrDefault).Where(line => !string.IsNullOrWhiteSpace(line)));
        // Icons come through as object-replacement or private-use glyphs on lines of their own.
        text = string.Join("\n", text.Split('\n')
            .Select(line => new string(line.Where(c => c != '￼' && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.PrivateUse).ToArray()).Trim())
            .Where(line => line.Length > 0));
        if (text.Length == 0) return CuaResult.Fail("The page has no readable text yet. Wait for it to load or use the screenshot.");
        if (text.Length > MaxPageText) text = text[..MaxPageText];
        return CuaResult.Ok($"Text of \"{window.Title}\"{(text.Length >= MaxPageText ? " (first 12,000 characters)" : "")}:\n{text}");
    }

    private CuaResult ListTabs(CuaToolCall call)
    {
        var windows = Win32Desktop.ListWindows()
            .Where(window => Browsers.Contains(window.AppId) && (call.App is null || Matches(window, call.App) > 0)).ToList();
        if (windows.Count == 0) return CuaResult.Ok("No browser windows are open.");
        var lines = new List<string>();
        foreach (var window in windows)
        {
            lines.Add($"window_id {window.Id} ({window.AppName}):");
            var tabs = Tabs(window);
            for (var index = 0; index < tabs.Length; index++)
                lines.Add($"  tab_index {index + 1}: \"{TabTitle(tabs[index])}\"{(IsSelected(tabs[index]) ? " [active]" : "")}");
        }
        return CuaResult.Ok(string.Join("\n", lines));
    }

    private CuaResult ActivateTab(CuaToolCall call)
    {
        var window = TargetWindow(call);
        var index = call.Integer("tab_index") ?? throw new ArgumentException("activate_browser_tab needs tab_index.");
        var tabs = Tabs(window);
        if (index < 1 || index > tabs.Length) return CuaResult.Fail($"That window has {tabs.Length} tabs. Call list_browser_tabs again.");
        var tab = tabs[index - 1];
        var title = TabTitle(tab);
        if (Press(tab, $"tab \"{title}\"") is null)
        {
            if (Focus(window) is { } failure) return failure;
            var bounds = tab.BoundingRectangle;
            WindowsInput.Click(bounds.X + bounds.Width / 2.0, bounds.Y + bounds.Height / 2.0);
        }
        return CuaResult.Ok($"Switched to the tab \"{title}\".");
    }

    /// <summary>The browser's own tabs. Web pages are skipped, which keeps their tabs out and the
    /// walk fast (a YouTube page has thousands of nodes).</summary>
    private AutomationElement[] Tabs(CuaWindow window)
    {
        var tabs = new List<AutomationElement>();
        var walker = _observer.Automation.TreeWalkerFactory.GetControlViewWalker();
        void Visit(AutomationElement parent, int depth)
        {
            if (depth > 30) return;
            for (var child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
            {
                var type = child.Properties.ControlType.ValueOrDefault;
                if (type == ControlType.TabItem) tabs.Add(child);
                else if (type != ControlType.Document) Visit(child, depth + 1);
            }
        }
        Visit(Root(window), 0);
        return tabs.ToArray();
    }

    private AutomationElement[] Documents(CuaWindow window) =>
        Root(window).FindAllDescendants(condition => condition.ByControlType(ControlType.Document));

    private AutomationElement Root(CuaWindow window) => _observer.Automation.FromHandle(new IntPtr(window.Id));

    private static double Area(AutomationElement element) =>
        element.BoundingRectangle is var bounds ? (double)bounds.Width * bounds.Height : 0;

    /// <summary>Chrome appends its hover-card details ("- Memory usage - 242 MB") to tab names.</summary>
    private static string TabTitle(AutomationElement tab) =>
        System.Text.RegularExpressions.Regex.Replace(tab.Properties.Name.ValueOrDefault ?? "", @" - Memory usage - [\d.,]+\s*[KMG]B$", "");

    private static bool IsSelected(AutomationElement tab) =>
        tab.Patterns.SelectionItem.TryGetPattern(out var selection) && selection.IsSelected.ValueOrDefault;

    private static string Describe(CuaElement info) =>
        info.Title.Length > 0 ? $"{info.Role} \"{info.Title}\" (e{info.ElementIndex})" : $"{info.Role} e{info.ElementIndex}";

    private static string LabelSuffix(CuaToolCall call) => call.String("label") is { } label ? $" ({label})" : "";

    private static string Truncate(string value, int limit) => value.Length > limit ? value[..(limit - 1)] + "…" : value;

    /// <summary>Hands the screen back: windows it un-minimized go back down, and when a window it
    /// worked in is still in front of the one the user was in, theirs comes back. Not after
    /// launch_app, whose app the user asked to see.</summary>
    public void Dispose()
    {
        Sleep();
        if (!_launched)
        {
            var foreground = Win32Desktop.ForegroundWindow();
            if (_userWindow != 0 && foreground != _userWindow && _touched.Contains(foreground) && Win32Desktop.IsAlive(_userWindow))
                Win32Desktop.Activate(_userWindow);
        }
        _observer.Dispose();
    }
}
