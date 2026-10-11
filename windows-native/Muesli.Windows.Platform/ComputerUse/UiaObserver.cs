using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using FlaUI.UIA3;

namespace Muesli.Windows.ComputerUse;

/// <summary>
/// Captures a target window as planner state: the interactive UI Automation elements (numbered),
/// visible text, focus and selection, and a screenshot with each element's number drawn on it
/// (set-of-marks). One cached tree walk keeps it fast and immune to providers that do not
/// implement a property. Password values are never read. Call from a background (MTA) thread.
/// </summary>
public sealed class UiaObserver : IDisposable
{
    public const int MaxElements = 120;
    public const int MaxTextLines = 80;
    public const int MaxDepth = 60;
    public const int MaxScreenshotWidth = 1280;

    private const int MaxCandidates = 1500;
    private static readonly HashSet<ControlType> RowTypes = [ControlType.ListItem, ControlType.TreeItem, ControlType.DataItem];

    private static readonly HashSet<ControlType> InteractiveTypes =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink,
        ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider, ControlType.Spinner,
        ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem, ControlType.Document,
    ];

    private readonly UIA3Automation _automation = new();
    private readonly Dictionary<int, AutomationElement> _elements = new();

    /// <summary>The latest captured state; element numbers refer to it.</summary>
    public CuaWindowState? Latest { get; private set; }
    public CuaWindow? LatestWindow { get; private set; }

    public AutomationElement? Element(int index) => _elements.GetValueOrDefault(index);

    public CuaWindowState Observe(CuaWindow window, bool includeScreenshot)
    {
        _elements.Clear();
        var handle = new IntPtr(window.Id);
        var frame = Win32Desktop.WindowFrame(handle) ?? window.Frame;
        var candidates = new List<Candidate>();
        var texts = new List<string>();
        var seenText = new HashSet<string>(StringComparer.Ordinal);

        using (BuildCache().Activate())
        {
            var root = _automation.FromHandle(handle);
            Walk(root, "0", 0, frame, window.ProcessId, candidates, texts, seenText);
        }
        var elements = Number(candidates);

        var focused = FocusedElement(window.ProcessId, frame, out var selectedText, out var documentText);
        CuaScreenshot? screenshot = null;
        if (includeScreenshot)
        {
            try { screenshot = Screenshot(frame, elements); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
        }

        LatestWindow = window with { Frame = frame, Title = Win32Desktop.WindowTitle(handle) };
        Latest = new CuaWindowState(
            CuaWindowState.NewStateId(), window.AppName, window.AppId, window.Id, LatestWindow.Title, frame, screenshot,
            Win32Desktop.CursorPosition(), focused, selectedText, documentText,
            texts.Count == 0 ? null : string.Join("\n", texts), elements, DateTimeOffset.UtcNow);
        return Latest;
    }

    private sealed record Candidate(AutomationElement Element, bool IsRow, CuaElement Data);

    /// <summary>Numbers candidates in tree order. Over the cap, every non-row control is kept
    /// before list rows so toolbars and address bars survive long file or message lists.</summary>
    private List<CuaElement> Number(List<Candidate> candidates)
    {
        IEnumerable<Candidate> kept = candidates;
        if (candidates.Count > MaxElements)
        {
            var controls = candidates.Count(candidate => !candidate.IsRow);
            var rowBudget = Math.Max(0, MaxElements - controls);
            var controlBudget = MaxElements - rowBudget;
            kept = candidates.Where(candidate => candidate.IsRow ? rowBudget-- > 0 : controlBudget-- > 0).ToList();
        }
        var elements = new List<CuaElement>();
        foreach (var candidate in kept)
        {
            var index = elements.Count + 1;
            _elements[index] = candidate.Element;
            elements.Add(candidate.Data with { ElementId = $"e{index}", ElementIndex = index });
        }
        return elements;
    }

    private CacheRequest BuildCache()
    {
        var library = _automation.PropertyLibrary;
        var cache = new CacheRequest { TreeScope = TreeScope.Subtree };
        foreach (var property in new PropertyId[]
        {
            library.Element.ControlType, library.Element.Name, library.Element.IsEnabled, library.Element.BoundingRectangle,
            library.Element.IsOffscreen, library.Element.IsPassword, library.Element.HelpText, library.Element.HasKeyboardFocus,
            library.Element.IsKeyboardFocusable, library.Element.ProcessId,
            _automation.PatternLibrary.InvokePattern.AvailabilityProperty, _automation.PatternLibrary.TogglePattern.AvailabilityProperty,
            _automation.PatternLibrary.ExpandCollapsePattern.AvailabilityProperty, _automation.PatternLibrary.SelectionItemPattern.AvailabilityProperty,
            _automation.PatternLibrary.ValuePattern.AvailabilityProperty, _automation.PatternLibrary.RangeValuePattern.AvailabilityProperty,
            _automation.PatternLibrary.ScrollPattern.AvailabilityProperty,
            library.Value.Value, library.Value.IsReadOnly, library.Toggle.ToggleState,
            library.ExpandCollapse.ExpandCollapseState, library.SelectionItem.IsSelected,
        })
        {
            cache.Add(property);
        }
        return cache;
    }

    private void Walk(AutomationElement element, string path, int depth, CuaRect window, int processId,
        List<Candidate> candidates, List<string> texts, HashSet<string> seenText)
    {
        if (depth > MaxDepth || candidates.Count >= MaxCandidates) return;
        var type = Get<ControlType>(element, _automation.PropertyLibrary.Element.ControlType);
        // Scrollbars and their buttons waste element slots; the scroll tool covers them.
        if (type == ControlType.ScrollBar) return;
        bool isRow;
        try { isRow = Classify(element, type, path, window, processId, candidates, texts, seenText); }
        catch (Exception exception) when (IsElementGone(exception)) { return; }
        if (isRow) return; // A row's cells are folded into its value.

        AutomationElement[] children;
        try { children = element.CachedChildren; }
        catch (Exception exception) when (IsElementGone(exception)) { return; }
        for (var index = 0; index < children.Length; index++)
            Walk(children[index], $"{path}.{index}", depth + 1, window, processId, candidates, texts, seenText);
    }

    /// <summary>Adds an interactive element (returns true when it is a list/tree/data row) or a
    /// line of visible text.</summary>
    private bool Classify(AutomationElement element, ControlType type, string path, CuaRect window, int processId,
        List<Candidate> candidates, List<string> texts, HashSet<string> seenText)
    {
        var library = _automation.PropertyLibrary;
        if (Get<bool>(element, library.Element.IsOffscreen)) return false;
        var bounds = Get<Rectangle>(element, library.Element.BoundingRectangle);
        if (bounds.Width <= 1 || bounds.Height <= 1) return false;
        var rect = new CuaRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        if (!Intersects(rect, window)) return false;

        var name = Clean(Get<string>(element, library.Element.Name));
        var password = Get<bool>(element, library.Element.IsPassword);
        var invoke = Get<bool>(element, _automation.PatternLibrary.InvokePattern.AvailabilityProperty);
        var toggle = Get<bool>(element, _automation.PatternLibrary.TogglePattern.AvailabilityProperty);
        var expand = Get<bool>(element, _automation.PatternLibrary.ExpandCollapsePattern.AvailabilityProperty);
        var select = Get<bool>(element, _automation.PatternLibrary.SelectionItemPattern.AvailabilityProperty);
        var hasValue = Get<bool>(element, _automation.PatternLibrary.ValuePattern.AvailabilityProperty);
        var editable = hasValue && !Get<bool>(element, library.Value.IsReadOnly);
        var range = Get<bool>(element, _automation.PatternLibrary.RangeValuePattern.AvailabilityProperty);
        var scroll = Get<bool>(element, _automation.PatternLibrary.ScrollPattern.AvailabilityProperty);
        var focusable = Get<bool>(element, library.Element.IsKeyboardFocusable);

        var interactive = invoke || toggle || expand || select || editable || range ||
                          (InteractiveTypes.Contains(type) && (type != ControlType.Document || focusable));
        if (!interactive)
        {
            if (name.Length > 0 && texts.Count < MaxTextLines && type is ControlType.Text or ControlType.Header or ControlType.HeaderItem
                    or ControlType.StatusBar or ControlType.TitleBar or ControlType.Image or ControlType.Group && seenText.Add(name))
                texts.Add(Truncate(name, 160));
            return false;
        }

        var isRow = RowTypes.Contains(type);
        var value = password ? "" : Clean(hasValue ? Get<string>(element, library.Value.Value) : "");
        if (value == name) value = "";
        if (isRow) value = string.Join(", ", new[] { value }.Concat(RowCells(element, name)).Where(part => part.Length > 0));
        var states = new List<string>();
        if (toggle) states.Add(Get<ToggleState>(element, library.Toggle.ToggleState) switch
            { ToggleState.On => "checked", ToggleState.Off => "unchecked", _ => "mixed" });
        if (expand) states.Add(Get<ExpandCollapseState>(element, library.ExpandCollapse.ExpandCollapseState) switch
            { ExpandCollapseState.Expanded => "expanded", ExpandCollapseState.Collapsed => "collapsed", _ => "" });
        if (select && Get<bool>(element, library.SelectionItem.IsSelected)) states.Add("selected");
        if (Get<bool>(element, library.Element.HasKeyboardFocus)) states.Add("focused");
        if (password) states.Add("password");

        var actions = new List<string> { "click" };
        if (toggle) actions.Add("toggle");
        if (expand) actions.AddRange(["expand", "collapse"]);
        if (select) actions.Add("select");
        if (editable && !password) actions.Add("set_value");
        if (range) actions.Add("set_range");
        if (scroll) actions.AddRange(["scroll_up", "scroll_down"]);

        candidates.Add(new Candidate(element, isRow, new CuaElement(
            "", 0, type.ToString(), Truncate(name, 80), string.Join(", ", states.Where(s => s.Length > 0)),
            Truncate(value, 160), Truncate(Clean(Get<string>(element, library.Element.HelpText)), 80),
            Get<bool>(element, library.Element.IsEnabled), rect, path, actions,
            Get<int>(element, library.Element.ProcessId) is var pid and > 0 ? pid : processId)));
        return isRow;
    }

    /// <summary>Cell text of a row ("Date modified: 11-10-2026"), two levels deep.</summary>
    private IEnumerable<string> RowCells(AutomationElement row, string rowName)
    {
        var library = _automation.PropertyLibrary;
        var cells = new List<string>();
        void Collect(AutomationElement parent, int depth)
        {
            AutomationElement[] children;
            try { children = parent.CachedChildren; }
            catch (Exception exception) when (IsElementGone(exception)) { return; }
            foreach (var child in children)
            {
                if (cells.Count >= 8) return;
                var label = Clean(Get<string>(child, library.Element.Name));
                var text = Get<bool>(child, library.Element.IsPassword) ? "" : Clean(Get<string>(child, library.Value.Value));
                if (text.Length > 0 && text != rowName) cells.Add(label.Length > 0 && label != text ? $"{label}: {text}" : text);
                else if (text.Length == 0 && label.Length > 0 && label != rowName) cells.Add(label);
                if (depth < 1) Collect(child, depth + 1);
            }
        }
        Collect(row, 0);
        return cells.Distinct();
    }

    private CuaFocusedElement? FocusedElement(int processId, CuaRect window, out string? selectedText, out string? documentText)
    {
        selectedText = null;
        documentText = null;
        try
        {
            var focused = _automation.FocusedElement();
            if (focused is null) return null;
            var bounds = focused.Properties.BoundingRectangle.ValueOrDefault;
            var rect = new CuaRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            if (focused.Properties.ProcessId.ValueOrDefault != processId && !Intersects(rect, window)) return null;
            var password = focused.Properties.IsPassword.ValueOrDefault;
            var value = "";
            if (!password)
            {
                if (focused.Patterns.Text.TryGetPattern(out var text))
                {
                    documentText = NullIfEmpty(Truncate(text.DocumentRange.GetText(4000), 4000));
                    var selection = text.GetSelection();
                    if (selection.Length > 0) selectedText = NullIfEmpty(Truncate(selection[0].GetText(2000), 2000));
                }
                if (focused.Patterns.Value.TryGetPattern(out var valuePattern))
                    value = Truncate(Clean(valuePattern.Value.ValueOrDefault ?? ""), 160);
            }
            return new CuaFocusedElement(focused.Properties.ControlType.ValueOrDefault.ToString(),
                Truncate(Clean(focused.Properties.Name.ValueOrDefault ?? ""), 80), password ? "password" : "", value, rect,
                focused.Properties.ProcessId.ValueOrDefault);
        }
        catch (Exception exception) when (IsElementGone(exception)) { return null; }
    }

    /// <summary>Window screenshot with every numbered element boxed and labelled, scaled to at most
    /// <see cref="MaxScreenshotWidth"/> wide, as a JPEG data URL.</summary>
    private static CuaScreenshot Screenshot(CuaRect frame, IReadOnlyList<CuaElement> elements)
    {
        using var capture = Win32Desktop.CaptureScreen(frame);
        var scale = Math.Min(1.0, (double)MaxScreenshotWidth / capture.Width);
        var width = Math.Max(1, (int)Math.Round(capture.Width * scale));
        var height = Math.Max(1, (int)Math.Round(capture.Height * scale));
        using var image = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(capture, 0, 0, width, height);
            DrawMarks(graphics, elements, frame, scale);
        }
        using var stream = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 80L) } };
        image.Save(stream, jpeg, parameters);
        return new CuaScreenshot("shot-" + Guid.NewGuid().ToString("N")[..8], width, height, frame, scale, scale)
        {
            ImageDataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray()),
        };
    }

    private static readonly Color[] MarkColors =
    [
        Color.FromArgb(230, 25, 75), Color.FromArgb(0, 130, 200), Color.FromArgb(60, 180, 75), Color.FromArgb(245, 130, 48),
        Color.FromArgb(145, 30, 180), Color.FromArgb(0, 150, 150), Color.FromArgb(200, 0, 200), Color.FromArgb(128, 96, 0),
    ];

    private static void DrawMarks(Graphics graphics, IReadOnlyList<CuaElement> elements, CuaRect frame, double scale)
    {
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var element in elements)
        {
            if (element.Frame is not { } rect) continue;
            // Page-sized containers stay in the element list but would only bury the real controls.
            if (rect.Width * rect.Height > frame.Width * frame.Height / 4) continue;
            var color = MarkColors[element.ElementIndex % MarkColors.Length];
            var box = new RectangleF((float)((rect.X - frame.X) * scale), (float)((rect.Y - frame.Y) * scale),
                (float)(rect.Width * scale), (float)(rect.Height * scale));
            using var pen = new Pen(color, 1.5f);
            graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            var label = element.ElementIndex.ToString();
            var size = graphics.MeasureString(label, font);
            var tag = new RectangleF(Math.Max(0, box.X), Math.Max(0, box.Y - size.Height + 2), size.Width, size.Height - 2);
            using var fill = new SolidBrush(color);
            graphics.FillRectangle(fill, tag);
            graphics.DrawString(label, font, Brushes.White, tag.X, tag.Y - 1);
        }
    }

    private static T Get<T>(AutomationElement element, PropertyId property)
    {
        try { return element.FrameworkAutomationElement.TryGetPropertyValue<T>(property, out var value) && value is not null ? value : default!; }
        catch (Exception exception) when (IsElementGone(exception) || exception is InvalidCastException) { return default!; }
    }

    private static bool Intersects(CuaRect a, CuaRect b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static bool IsElementGone(Exception exception) =>
        exception is FlaUI.Core.Exceptions.ElementNotAvailableException or FlaUI.Core.Exceptions.PropertyNotSupportedException
            or System.Runtime.InteropServices.COMException or TimeoutException or UnauthorizedAccessException;

    /// <summary>Collapses whitespace and drops invisible format marks (Explorer wraps dates in
    /// left-to-right marks).</summary>
    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : string.Join(' ', new string(value.Where(c =>
            char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int limit) => value.Length > limit ? value[..(limit - 1)] + "…" : value;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public void Dispose() => _automation.Dispose();
}
