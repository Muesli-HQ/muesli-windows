using System.Text.Json.Nodes;

namespace Muesli.Windows.ComputerUse;

/// <summary>One tool the planner can call, with its JSON schema.</summary>
public sealed record CuaToolDefinition(string Name, string Description, JsonObject Parameters, bool Mutating)
{
    public IEnumerable<string> Required => (Parameters["required"] as JsonArray ?? []).Select(item => item!.ToString());
}

/// <summary>
/// The Windows tool catalog: the macOS <c>muesli-cua-tools-v1</c> desktop tools with the same
/// names and arguments, plus <c>activate_window</c> and <c>wait_for</c>.
/// </summary>
public static class CuaCatalog
{
    public const string Version = "muesli-cua-tools-windows-v1";

    private static readonly JsonObject ElementIndex = Integer("Element number from the latest state (the numbered box on the screenshot).");
    private static readonly JsonObject Label = String("What the target is, for example \"Send button\". Used for safety checks and the trace.");
    private static readonly JsonObject App = String("App name as shown by list_apps, for example Notepad or Google Chrome.");

    public static readonly IReadOnlyList<CuaToolDefinition> Tools =
    [
        Tool("list_apps", "List installed and running apps.", false, []),
        Tool("launch_app", "Open an app from the Start menu, or bring it to the front if it is already open. Prefer this over clicking through the taskbar or Start menu.", true,
            ["app_name"], ("app_name", App)),
        Tool("list_windows", "List open windows with their window_id, app and title.", false, [], ("app_name", String("Optional app to filter by."))),
        Tool("activate_window", "Bring a window to the front by window_id or app_name.", true, [],
            ("window_id", Integer("Window id from list_windows.")), ("app_name", App)),
        Tool("get_app_state", "Capture a fresh screenshot and element list of an app's window. The state already comes with every step; call this only to switch to another app or window.", false, [],
            ("app_name", App), ("window_id", Integer("Optional window id from list_windows."))),
        Tool("click_element", "Click a numbered element from the latest state. Use this whenever the target has a number.", true, ["element_index"],
            ("element_index", ElementIndex), ("clicks", Integer("1 for a single click, 2 for a double click.")),
            ("button", Enum("Mouse button.", "left", "right")), ("label", Label)),
        Tool("click_point", "Click a screenshot pixel when the target has no number. Coordinates are pixels of the latest screenshot.", true, ["screenshot_id", "x", "y", "label"],
            ("screenshot_id", String("screenshot_id of the latest state.")), ("x", Number("Screenshot x.")), ("y", Number("Screenshot y.")),
            ("clicks", Integer("1 for a single click, 2 for a double click.")), ("button", Enum("Mouse button.", "left", "right")), ("label", Label)),
        Tool("move_cursor", "Move the mouse to a screenshot pixel without clicking, for example to reveal a hover menu.", true, ["screenshot_id", "x", "y"],
            ("screenshot_id", String("screenshot_id of the latest state.")), ("x", Number("Screenshot x.")), ("y", Number("Screenshot y."))),
        Tool("perform_secondary_action", "Run one of an element's listed actions other than click: expand, collapse, toggle, select, show_menu, scroll_up, scroll_down, increment, decrement, focus.", true,
            ["element_index", "action_name"], ("element_index", ElementIndex), ("action_name", String("An action from the element's actions list.")), ("label", Label)),
        Tool("set_value", "Replace the whole value of an editable or range element, for example a search box or slider.", true, ["element_index", "value"],
            ("element_index", ElementIndex), ("value", String("New value.")), ("label", Label)),
        Tool("type_text", "Type exact text at the keyboard focus. Pass element_index to click into that field first. \\n presses Enter.", true, ["text"],
            ("text", String("Text to type.")), ("element_index", ElementIndex), ("app_name", App)),
        Tool("paste_text", "Paste exact text through the clipboard (the user's clipboard is restored). Faster than type_text for more than a few words. Pass element_index to click into that field first.", true, ["text"],
            ("text", String("Text to paste.")), ("element_index", ElementIndex), ("app_name", App)),
        Tool("press_key", "Press a key or shortcut, for example key \"enter\", or key \"s\" with modifiers [\"ctrl\"]. Prefer shortcuts when they exist. The Windows key is not available.", true, ["key"],
            ("key", String("Key name: a character, enter, tab, escape, backspace, delete, up, down, left, right, home, end, page_up, page_down, space, F1-F24.")),
            ("modifiers", new JsonObject { ["type"] = "array", ["description"] = "Modifier keys held during the press.", ["items"] = Enum("Modifier.", "ctrl", "alt", "shift") }),
            ("app_name", App)),
        Tool("scroll", "Scroll an element, or the window when no element is given.", true, ["direction"],
            ("direction", Enum("Scroll direction.", "up", "down", "left", "right")), ("pages", Number("How far, in pages. Default 1.")), ("element_index", ElementIndex)),
        Tool("drag", "Drag the mouse between two screenshot pixels.", true, ["screenshot_id", "x", "y", "to_x", "to_y"],
            ("screenshot_id", String("screenshot_id of the latest state.")), ("x", Number("Start x.")), ("y", Number("Start y.")),
            ("to_x", Number("End x.")), ("to_y", Number("End y.")), ("label", Label)),
        Tool("wait_for", "Wait until text appears in the target window (title, element names, values or visible text), for example after a page or dialog starts loading.", false, ["text"],
            ("text", String("Text to wait for.")), ("app_name", App), ("timeout_seconds", Number("At most 15. Default 5."))),
        Tool("page_get_text", "Read the text of the web page (or document) in a browser window, up to 12,000 characters, including parts scrolled out of view. Use it to read articles, emails or results instead of scrolling.", false, [],
            ("app_name", App), ("window_id", Integer("Optional window id from list_windows or list_browser_tabs."))),
        Tool("list_browser_tabs", "List the tabs of open browser windows (Chrome, Edge, Firefox and others) by title.", false, [], ("app_name", App)),
        Tool("activate_browser_tab", "Switch a browser window to one of its tabs, using window_id and tab_index from list_browser_tabs.", true, ["window_id", "tab_index"],
            ("window_id", Integer("Window id from list_browser_tabs.")), ("tab_index", Integer("tab_index from list_browser_tabs."))),
        Tool("get_muesli_settings", "List Muesli's own settings that can be changed by voice, with current values and choices.", false, []),
        Tool("set_muesli_setting", "Change one of Muesli's own settings. Call get_muesli_settings first and use a listed setting and choice id.", false, ["setting", "value"],
            ("setting", String("Setting id from get_muesli_settings.")), ("value", String("Choice id from get_muesli_settings."))),
        Tool("ask_user", "Ask the user a short question when the choice is theirs or the command is ambiguous (which contact, which file, save or discard). Do not ask for permission to click risky buttons; Muesli asks for that itself.", false, ["question"],
            ("question", String("One short question.")),
            ("options", new JsonObject { ["type"] = "array", ["description"] = "Two to four short answers to offer as buttons. The user can also type another answer.", ["items"] = String("An answer.") })),
        Tool("finish","The task is complete. reason is the short answer or result shown to the user.", false, ["reason"], ("reason", String("Final result for the user."))),
        Tool("fail", "Stop because the task is blocked, unsafe or impossible. reason explains why.", false, ["reason"], ("reason", String("Why the task cannot be done."))),
    ];

    private static readonly Dictionary<string, CuaToolDefinition> ByName = Tools.ToDictionary(tool => tool.Name);

    /// <summary>Names older planners use for catalog tools.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["get_window_state"] = "get_app_state", ["hotkey"] = "press_key", ["click"] = "click_element",
    };

    public static CuaToolDefinition? Find(string name) =>
        ByName.GetValueOrDefault(Aliases.GetValueOrDefault(name, name));

    /// <summary>Checks the call against the catalog; returns the problem, or null when valid.</summary>
    public static string? Validate(CuaToolCall call)
    {
        if (Find(call.Name) is not { } tool) return $"Unknown tool {call.Name}.";
        if (call.Name == "click" && call.ElementIndex is null) tool = ByName["click_point"];
        foreach (var key in tool.Required)
        {
            var present = key == "element_index" ? call.ElementIndex is not null : call.Arguments[key] is not null;
            if (!present) return $"{tool.Name} requires {key}.";
        }
        return null;
    }

    /// <summary>The catalog as Responses API function tools.</summary>
    public static JsonArray ResponsesTools() =>
        new(Tools.Select(tool => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = tool.Parameters.DeepClone(),
        }).ToArray());

    private static CuaToolDefinition Tool(string name, string description, bool mutating, string[] required, params (string Key, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (key, schema) in properties) props[key] = schema.DeepClone();
        return new CuaToolDefinition(name, description, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(key => (JsonNode)key).ToArray()),
            ["additionalProperties"] = false,
        }, mutating);
    }

    private static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };
    private static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray()) };
}
