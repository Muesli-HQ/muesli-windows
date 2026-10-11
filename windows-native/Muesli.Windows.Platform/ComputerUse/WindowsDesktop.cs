using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Muesli.Windows.ComputerUse;

/// <summary>
/// Windows implementation of the Computer Use desktop: resolves the target window, observes it,
/// and executes desktop tools with UI Automation and synthetic input. Every input action first
/// brings its target window to the front and refuses apps on the safety deny-list.
/// </summary>
public sealed class WindowsDesktop : ICuaDesktop
{
    private readonly UiaObserver _observer = new();
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
        // Screenshots copy screen pixels, so the window has to be in front.
        if (includeScreenshot) Win32Desktop.Activate(window.Id);
        return _observer.Observe(window, includeScreenshot);
    }

    public CuaResult Execute(CuaToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
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
        if (windowId is { } id) return Win32Desktop.Describe(new IntPtr(id));
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
        return Win32Desktop.Activate(window.Id) ? null : CuaResult.Fail($"Windows did not let Muesli bring {window.AppName} to the front.");
    }

    /// <summary>Input goes to the target window: the named app, else the current target.</summary>
    private CuaWindow PrepareInput(CuaToolCall call)
    {
        var window = (call.App is { } app ? BestWindow(app) : null) ?? _target
                     ?? throw new InvalidOperationException("No target window yet. Call get_app_state or launch_app first.");
        if (!Win32Desktop.IsAlive(window.Id)) throw new InvalidOperationException($"{window.AppName}'s window has closed.");
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

    private CuaResult ClickElement(CuaToolCall call)
    {
        var window = PrepareInput(call);
        var (element, info) = Element(call);
        var button = call.String("button") ?? "left";
        var clicks = call.Integer("clicks") ?? 1;
        var label = Describe(info);
        var (x, y) = Center(element, info);
        if (Win32Desktop.RootWindowAt(x, y) == window.Id)
        {
            WindowsInput.Click(x, y, button, clicks);
            return CuaResult.Ok($"Clicked {label}.");
        }
        // Covered or scrolled away: act through UI Automation instead of clicking whatever is on top.
        if (button == "left" && clicks == 1)
        {
            if (element.Patterns.Invoke.TryGetPattern(out var invoke)) { invoke.Invoke(); return CuaResult.Ok($"Pressed {label}."); }
            if (element.Patterns.Toggle.TryGetPattern(out var toggle)) { toggle.Toggle(); return CuaResult.Ok($"Toggled {label}."); }
            if (element.Patterns.SelectionItem.TryGetPattern(out var selection)) { selection.Select(); return CuaResult.Ok($"Selected {label}."); }
            if (element.Patterns.ExpandCollapse.TryGetPattern(out var expand))
            {
                if (expand.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Expanded) expand.Collapse(); else expand.Expand();
                return CuaResult.Ok($"Expanded or collapsed {label}.");
            }
        }
        return CuaResult.Fail($"{label} is covered by another window or off screen. Scroll it into view or close what covers it.");
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
        PrepareInput(call);
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
        PrepareInput(call);
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
        var window = PrepareInput(call);
        var direction = (call.String("direction") ?? "down").ToLowerInvariant();
        var pages = call.Number("pages") ?? 1;
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

    private static string Describe(CuaElement info) =>
        info.Title.Length > 0 ? $"{info.Role} \"{info.Title}\" (e{info.ElementIndex})" : $"{info.Role} e{info.ElementIndex}";

    private static string LabelSuffix(CuaToolCall call) => call.String("label") is { } label ? $" ({label})" : "";

    private static string Truncate(string value, int limit) => value.Length > limit ? value[..(limit - 1)] + "…" : value;

    public void Dispose() => _observer.Dispose();
}
