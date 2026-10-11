using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Muesli.Windows.ComputerUse;

/// <summary>What the planner sees each step: the command, the latest window state and every
/// earlier tool call with its result (the conversation so far).</summary>
public sealed record CuaPlannerRequest(string Command, int Step, int MaxSteps, CuaWindowState State, IReadOnlyList<CuaToolOutcome> History);

/// <summary>The planner's answer: one or more tool calls, run in order.</summary>
public sealed record CuaPlan(IReadOnlyList<CuaToolCall> Calls, string? Text = null);

public sealed record CuaToolOutcome(int Step, CuaToolCall Call, CuaResultStatus Status, string Message);

public interface ICuaPlanner
{
    /// <summary>Plans the next tool calls. Throws <see cref="CuaInvalidToolCallException"/> for
    /// a malformed call, which the runtime turns into a repair hint.</summary>
    Task<CuaPlan> PlanAsync(CuaPlannerRequest request, CancellationToken cancellationToken);
}

public enum CuaRunStatus { Done, Failed, NeedsConfirmation, Cancelled, TimedOut }

public sealed record CuaRunResult(CuaRunStatus Status, string Message, IReadOnlyList<CuaTraceEvent> Trace);

public sealed record CuaTraceEvent(string Kind, string Title, string Body, int? Step);

public sealed record CuaRuntimeOptions
{
    public int MaxSteps { get; init; } = 50;
    /// <summary>Time spent observing and acting; planning time does not count.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Pause after actions before observing, so the UI can react.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    /// <summary>Asks the user to approve a risky action. Without it, the run stops instead.</summary>
    public Func<string, CancellationToken, Task<bool>>? Confirm { get; init; }
    /// <summary>Muesli's own settings, for "change my dictation model" commands.</summary>
    public CuaMuesliSettings? Settings { get; init; }
    public Action<string>? OnStatus { get; init; }
    public Action<CuaTraceEvent>? OnEvent { get; init; }
}

/// <summary>
/// The Computer Use step loop: observe, plan, run the planned calls, verify the UI changed, repeat
/// until finish/fail. Ported from macOS <c>ComputerUsePlannerRuntime</c>, plus multi-call steps,
/// in-run confirmation, <c>wait_for</c>, and recovery from failed tools instead of stopping.
/// Desktop calls block, so run this off the UI thread.
/// </summary>
public sealed class CuaRuntime(ICuaDesktop desktop, ICuaPlanner planner, CuaRuntimeOptions? options = null)
{
    private const int MaxRepairs = 2;
    private const int MaxConsecutiveFailures = 3;
    private const int MaxUnchangedObservations = 4;

    private static readonly HashSet<string> RiskyWords =
    [
        // macOS list, plus OpenAI's Windows confirmation categories (installing, publishing, money).
        "archive", "buy", "cancel", "checkout", "confirm", "delete", "discard", "pay", "purchase", "remove", "send", "submit", "unsubscribe",
        "install", "uninstall", "erase", "reset", "post", "publish", "transfer", "order",
        // A window's Close button does what Alt+F4 does, and can drop unsaved work.
        "close", "quit",
    ];

    private readonly CuaRuntimeOptions _options = options ?? new CuaRuntimeOptions();
    private readonly List<CuaTraceEvent> _trace = [];

    public async Task<CuaRunResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        _trace.Clear();
        Trace("command", "Command", command, null);
        var history = new List<CuaToolOutcome>();
        var unchangedActions = new Dictionary<string, int>();
        var unchangedObservations = 0;
        var repairs = 0;
        var failures = 0;
        var actionTime = Stopwatch.StartNew();

        Status("Looking at the screen");
        var state = desktop.Observe(null, null, true);
        TraceState(state, null);

        for (var step = 1; ; step++)
        {
            if (cancellationToken.IsCancellationRequested) return End(CuaRunStatus.Cancelled, "Cancelled.", step);
            if (actionTime.Elapsed > _options.Timeout) return End(CuaRunStatus.TimedOut, "Computer Use ran out of time.", step);
            if (step > _options.MaxSteps) return End(CuaRunStatus.Failed, "Computer Use reached its step limit.", step);

            CuaPlan plan;
            Status($"Planning step {step}");
            actionTime.Stop();
            try
            {
                plan = await planner.PlanAsync(new CuaPlannerRequest(command, step, _options.MaxSteps, state, history.ToArray()), cancellationToken);
                if (plan.Calls.Count == 0) throw new CuaInvalidToolCallException("", "", "The planner returned no tool call.");
            }
            catch (CuaInvalidToolCallException exception)
            {
                if (++repairs > MaxRepairs) return End(CuaRunStatus.Failed, $"The planner kept returning invalid tool calls: {exception.Message}", step);
                var call = new CuaToolCall(exception.ToolName.Length > 0 ? exception.ToolName : "fail", []);
                history.Add(new CuaToolOutcome(step, call, CuaResultStatus.Failed,
                    $"Invalid tool call: {exception.Message} Call exactly one catalog tool with arguments that match its schema."));
                Trace("repair", "Invalid tool call", exception.Message, step);
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return End(CuaRunStatus.Cancelled, "Cancelled.", step);
            }
            catch (Exception exception)
            {
                return End(CuaRunStatus.Failed, $"The planner failed: {exception.Message}", step);
            }
            finally { actionTime.Start(); }

            if (!string.IsNullOrWhiteSpace(plan.Text)) Trace("model_output", "Model", plan.Text!, step);
            var before = state;
            var acted = false;
            var observed = false;
            var stepFailed = false;
            foreach (var call in plan.Calls)
            {
                if (cancellationToken.IsCancellationRequested) return End(CuaRunStatus.Cancelled, "Cancelled.", step);
                Trace("tool_call", "Tool call", call.ToString(), step);
                if (CuaCatalog.Validate(call) is { } invalid)
                {
                    history.Add(new CuaToolOutcome(step, call, CuaResultStatus.Failed, $"Invalid tool call: {invalid}"));
                    if (++repairs > MaxRepairs) return End(CuaRunStatus.Failed, $"The planner kept returning invalid tool calls: {invalid}", step);
                    stepFailed = true;
                    break;
                }
                repairs = 0;
                var tool = CuaCatalog.Find(call.Name)!;

                if (tool.Name == "finish")
                {
                    var reason = call.String("reason") ?? "Done.";
                    return FinishLooksFailed(reason)
                        ? End(CuaRunStatus.Failed, reason, step)
                        : End(CuaRunStatus.Done, reason, step);
                }
                if (tool.Name == "fail") return End(CuaRunStatus.Failed, call.String("reason") ?? "Failed.", step);

                if (tool.Name == "get_app_state")
                {
                    Status("Looking at the screen");
                    state = desktop.Observe(call.App, call.Integer("window_id"), true);
                    TraceState(state, step);
                    history.Add(new CuaToolOutcome(step, call, CuaResultStatus.Executed, $"Captured state of {state.AppName} - {state.WindowTitle}."));
                    observed = true;
                    break; // Element numbers changed; later calls in this step would point at the old state.
                }
                if (tool.Name == "wait_for")
                {
                    var found = WaitFor(call, cancellationToken);
                    history.Add(new CuaToolOutcome(step, call, found.Status, found.Message));
                    acted = true; // Forces a fresh observation with a screenshot.
                    if (!found.Succeeded) stepFailed = true;
                    break;
                }

                if (RiskReason(call, before) is { } risk)
                {
                    var question = $"Allow Muesli to {Summary(call, before)}? ({risk})";
                    Trace("confirm", "Confirmation", question, step);
                    if (_options.Confirm is null) return End(CuaRunStatus.NeedsConfirmation, question, step);
                    Status("Waiting for your OK");
                    actionTime.Stop();
                    bool approved;
                    try { approved = await _options.Confirm(question, cancellationToken); }
                    catch (OperationCanceledException) { return End(CuaRunStatus.Cancelled, "Cancelled.", step); }
                    finally { actionTime.Start(); }
                    if (!approved) return End(CuaRunStatus.Cancelled, $"You declined: {Summary(call, before)}.", step);
                }

                Status(StatusFor(tool.Name));
                CuaResult result;
                try
                {
                    result = tool.Name is "get_muesli_settings" or "set_muesli_setting"
                        ? _options.Settings?.Execute(call) ?? new CuaResult(CuaResultStatus.Unsupported, "Muesli settings are not available here.")
                        : desktop.Execute(call, cancellationToken);
                }
                catch (OperationCanceledException) { return End(CuaRunStatus.Cancelled, "Cancelled.", step); }
                Trace("tool_result", result.Status.ToString(), result.Message, step);
                history.Add(new CuaToolOutcome(step, call, result.Status, result.Message));
                if (result.Status == CuaResultStatus.Cancelled) return End(CuaRunStatus.Cancelled, "Cancelled.", step);
                acted |= tool.Mutating;
                if (!result.Succeeded)
                {
                    stepFailed = true;
                    break;
                }
            }

            failures = stepFailed ? failures + 1 : 0;
            if (failures >= MaxConsecutiveFailures)
                return End(CuaRunStatus.Failed, history[^1].Message, step);

            if (acted)
            {
                unchangedObservations = 0;
                Thread.Sleep(_options.SettleDelay);
                Status("Looking at the screen");
                state = desktop.Observe(null, null, true);
                TraceState(state, step);
                if (stepFailed) continue;
                var changed = Signature(before) != Signature(state);
                var key = string.Join(";", plan.Calls.Select(call => call.ToString()));
                Annotate(history, changed
                    ? $"Verification: the window changed (now {state.AppName} - {state.WindowTitle})."
                    : "Verification: nothing visible changed. Check the target, or try a different approach.");
                if (changed) unchangedActions.Clear();
                else if ((unchangedActions[key] = unchangedActions.GetValueOrDefault(key) + 1) >= 2)
                    return End(CuaRunStatus.Failed, "Stopped repeating an action that changed nothing.", step);
            }
            else if (observed)
            {
                if (Signature(before) != Signature(state)) unchangedObservations = 0;
                else if (++unchangedObservations >= MaxUnchangedObservations)
                    return End(CuaRunStatus.Failed, "Stopped observing the same unchanged window repeatedly.", step);
            }
        }
    }

    private CuaResult WaitFor(CuaToolCall call, CancellationToken cancellationToken)
    {
        var text = call.String("text") ?? "";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(call.Number("timeout_seconds") ?? 5, 0.5, 15));
        Status($"Waiting for \"{text}\"");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = desktop.Observe(call.App, null, false);
            if (Contains(state, text)) return CuaResult.Ok($"\"{text}\" appeared after {clock.Elapsed.TotalSeconds:0.0}s.");
            if (clock.Elapsed > timeout) return CuaResult.Fail($"\"{text}\" did not appear within {timeout.TotalSeconds:0}s.");
            Thread.Sleep(400);
        }
    }

    private static bool Contains(CuaWindowState state, string text) =>
        new[] { state.WindowTitle, state.VisibleText, state.DocumentText, state.FocusedElement?.Value }
            .Concat(state.Elements.SelectMany(element => new[] { element.Title, element.Value }))
            .Any(value => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Why this call needs the user's OK, or null. Clicks are judged by the element's
    /// real name, not just the label the model chose.</summary>
    internal static string? RiskReason(CuaToolCall call, CuaWindowState state)
    {
        var tool = CuaCatalog.Find(call.Name)?.Name;
        var element = call.ElementIndex is { } index ? state.Elements.FirstOrDefault(e => e.ElementIndex == index) : null;
        switch (tool)
        {
            case "click_element" or "click_point" or "perform_secondary_action" or "drag" or "set_value":
                var name = $"{element?.Title} {call.String("label")}";
                if (Regex.IsMatch(name, @"\bdo(n['’]t| not) save\b", RegexOptions.IgnoreCase)) return "it throws away unsaved work";
                var words = Words(name);
                return words.FirstOrDefault(RiskyWords.Contains) is { } word ? $"it looks like \"{word}\"" : null;
            case "press_key":
                var key = (call.String("key") ?? "").ToLowerInvariant();
                var modifiers = call.Strings("modifiers").Select(m => m.ToLowerInvariant()).ToArray();
                var closes = (key == "f4" && modifiers.Contains("alt"))
                    || (key is "w" or "q" && modifiers.Any(m => m is "ctrl" or "control" or "cmd" or "command"));
                return closes ? "it closes a window or app" : null;
            default:
                return null;
        }
    }

    private static IEnumerable<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(word => word.Length > 0);

    internal static bool FinishLooksFailed(string reason)
    {
        var lowered = reason.ToLowerInvariant();
        string[] patterns =
        [
            @"^\s*(blocked|failed|unsupported|incomplete|not completed?)\s*[.!]?\s*$",
            @"\b(requires|needs)\s+confirmation\b",
            @"\b(task|request|command|workflow)\s+(is\s+)?(blocked|incomplete|not completed?|failed|unsupported)\b",
            @"\b(cannot|can't|could not|unable to|was not able to)\s+(complete|finish|perform|do|continue|proceed|access|open|click|type|paste|navigate|find)\b",
            @"\b(did not|didn't)\s+(complete|finish|perform|send|post|open|click|type|paste|navigate|find)\b",
            @"\b(permission|permissions)\s+(required|needed|denied|missing|not granted)\b",
            @"\b(not authorized|not allowed|access denied)\b",
            @"\bfailed to\s+(complete|finish|perform|open|click|type|paste|navigate|send|post)\b",
        ];
        return patterns.Any(pattern => Regex.IsMatch(lowered, pattern));
    }

    private static string Summary(CuaToolCall call, CuaWindowState state)
    {
        var element = call.ElementIndex is { } index ? state.Elements.FirstOrDefault(e => e.ElementIndex == index) : null;
        var target = element is { Title.Length: > 0 } ? $"\"{element.Title}\"" : call.String("label") is { } label ? $"\"{label}\"" : "the screen";
        return CuaCatalog.Find(call.Name)?.Name switch
        {
            "press_key" => $"press {string.Join("+", call.Strings("modifiers").Append(call.String("key") ?? ""))} in {state.AppName}",
            "set_value" => $"set {target} to \"{call.String("value")}\"",
            "drag" => $"drag {target}",
            _ => $"click {target} in {state.AppName}",
        };
    }

    private static string StatusFor(string tool) => tool switch
    {
        "launch_app" => "Opening app",
        "activate_window" => "Switching window",
        "click_element" or "click_point" => "Clicking",
        "type_text" => "Typing",
        "paste_text" => "Pasting",
        "press_key" => "Pressing keys",
        "scroll" => "Scrolling",
        "drag" => "Dragging",
        "set_value" => "Setting value",
        _ => "Working",
    };

    /// <summary>Cheap fingerprint of what the user would see change.</summary>
    internal static string Signature(CuaWindowState state) => string.Join("|",
        state.AppId, state.WindowId, state.WindowTitle, state.Elements.Count,
        state.FocusedElement?.Role, state.FocusedElement?.Value, state.SelectedText, state.DocumentText?.Length, state.DocumentText?.GetHashCode(),
        state.WindowFrame, string.Join(";", state.Elements.Take(24).Select(e => $"{e.Role}:{e.Title}:{e.Value}:{e.Label}:{e.Frame}")));

    private static void Annotate(List<CuaToolOutcome> history, string note)
    {
        if (history.Count > 0) history[^1] = history[^1] with { Message = $"{history[^1].Message} {note}" };
    }

    private void TraceState(CuaWindowState state, int? step) =>
        Trace("observation", "Observed", $"{state.AppName} - {state.WindowTitle} - {state.Elements.Count} elements" +
            (state.Screenshot is { } shot ? $" - screenshot {shot.ScreenshotId} {shot.Width}x{shot.Height}" : ""), step);

    private CuaRunResult End(CuaRunStatus status, string message, int step)
    {
        Trace(status.ToString().ToLowerInvariant(), status.ToString(), message, step);
        Status(status switch { CuaRunStatus.Done => "Done", CuaRunStatus.Cancelled => "Cancelled", _ => "Stopped" });
        return new CuaRunResult(status, message, _trace.ToArray());
    }

    private void Trace(string kind, string title, string body, int? step)
    {
        var item = new CuaTraceEvent(kind, title, body, step);
        _trace.Add(item);
        _options.OnEvent?.Invoke(item);
    }

    private void Status(string status) => _options.OnStatus?.Invoke(status);
}
