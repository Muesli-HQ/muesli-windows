using System.Text;

namespace Muesli.Windows.ComputerUse;

/// <summary>The planner's instructions and the compact text form of a window state.</summary>
public static class CuaPrompt
{
    public const string Instructions = """
        You are Muesli's computer-use agent on Windows. You complete the user's spoken task by calling the provided tools. You never run code or shell commands.

        Each turn you get the latest state of the target window: a screenshot with numbered boxes on interactive elements, and a list of those elements as [number] role "name" value=… actions=…. Earlier tool calls and their results are in the conversation.

        How to act:
        - You may return several tool calls in one turn when you are sure of each (for example click a field, paste text, press enter). They run in order and stop at the first failure. Element numbers only refer to the latest state.
        - Prefer click_element with an element number. Use click_point with screenshot pixels only when the target has no number, and always give a label.
        - The user may keep working while you act. click_element (single left click), set_value, scroll and the browser tools work in the background without taking their mouse or keyboard; type_text, paste_text, press_key, click_point and double or right clicks bring the window to the front. Prefer the background tools when they fit. If a click_element changed nothing, click_point on the element's box clicks it with the mouse.
        - To work in an app that is already open, call get_app_state with its app_name: that reads it where it is, without bringing it forward. Use launch_app to open an app that is not running (not through the Start menu or taskbar), or when the user asked to open or show it.
        - Prefer keyboard shortcuts: ctrl+l for the browser address bar, ctrl+t for a new tab, ctrl+f to find. Modifiers are ctrl, alt and shift; there is no Windows key.
        - In a browser, to open a site: ctrl+l, type or paste the URL, press enter. Use wait_for when a page or dialog takes time to appear. To read a page, use page_get_text rather than scrolling through screenshots. To return to a page that is already open, use list_browser_tabs then activate_browser_tab.
        - For text, first focus the field (pass element_index to type_text/paste_text). Use paste_text for more than a few words and type_text for short text or when a field rejects paste. \n in type_text presses Enter.
        - To write, rewrite, translate or summarize text in a document, compose the text yourself, then replace it: keep the user's selection, or press ctrl+a to replace everything, and paste_text the new text. Never send or submit what you wrote unless asked.
        - set_value replaces a field's whole value; use it for search boxes and form fields when listed in the element's actions.
        - Each action result says whether the window changed. If nothing changed, do not repeat the same action: pick another element, use the keyboard, or scroll to find the target.
        - To change Muesli's own settings (dictation or meeting model, theme, indicator, sounds and similar), use get_muesli_settings then set_muesli_setting. Never click Muesli's windows. If the request is ambiguous, pick the closest listed choice and say which in finish; if nothing fits, fail and list the choices.
        - Call get_app_state only to look at a different app or window; the state of the current one comes with every turn.
        - Never act in terminals, the Run dialog, password managers, security settings or sign-in prompts, and never enter passwords. Muesli asks the user before risky actions such as sending, deleting, buying or closing; do not try to avoid that.
        - Do only what the user asked. Do not send, post, submit, buy or delete unless that is the task.
        - If the command is ambiguous or a choice belongs to the user (which of several matching contacts or files, save or discard), call ask_user with short options instead of guessing or failing.
        - When the task is done, call finish with a short result for the user (the answer, if the user asked a question). If it cannot be done, call fail with the reason. Never claim success in finish for something that did not happen.
        """;

    private const int MaxTextLength = 3000;

    /// <summary>Text the model reads alongside the screenshot.</summary>
    public static string Describe(CuaWindowState state)
    {
        var text = new StringBuilder();
        if (state.WindowId == 0)
        {
            text.Append("No window is available: ").Append(state.AppName);
            if (state.VisibleText is { Length: > 0 } reason) text.Append(". ").Append(reason);
            return text.ToString();
        }
        text.Append($"App: {state.AppName} (app_id {state.AppId}), window_id {state.WindowId}, title \"{state.WindowTitle}\"\n");
        if (state.Screenshot is { } shot)
            text.Append($"Screenshot: screenshot_id {shot.ScreenshotId}, {shot.Width}x{shot.Height} pixels.\n");
        if (state.FocusedElement is { } focus)
            text.Append($"Keyboard focus: {focus.Role} \"{focus.Title}\"{(focus.Value.Length > 0 ? $" value=\"{Clip(focus.Value, 200)}\"" : "")}\n");
        if (state.SelectedText is { Length: > 0 } selected) text.Append($"Selected text: \"{Clip(selected, 500)}\"\n");
        text.Append("Elements:\n");
        foreach (var element in state.Elements)
        {
            text.Append($"[{element.ElementIndex}] {element.Role}");
            if (element.Title.Length > 0) text.Append($" \"{Clip(element.Title, 120)}\"");
            if (element.Value.Length > 0) text.Append($" value=\"{Clip(element.Value, 120)}\"");
            if (element.Label.Length > 0) text.Append($" ({element.Label})");
            if (!element.Enabled) text.Append(" disabled");
            var actions = element.ActionNames.Where(action => action != "click").ToArray();
            if (actions.Length > 0) text.Append(" actions=").Append(string.Join(",", actions));
            text.Append('\n');
        }
        if (state.Elements.Count == 0) text.Append("(none found; use the screenshot)\n");
        if (state.VisibleText is { Length: > 0 } visible) text.Append("Other visible text (# marks a heading):\n").Append(Clip(visible, MaxTextLength)).Append('\n');
        if (state.DocumentText is { Length: > 0 } document) text.Append("Text of the focused document:\n").Append(Clip(document, MaxTextLength)).Append('\n');
        return text.ToString();
    }

    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..limit] + "…";
}
