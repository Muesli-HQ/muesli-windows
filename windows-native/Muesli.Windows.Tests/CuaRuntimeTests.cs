using System.Text.Json;
using Muesli.Windows.ComputerUse;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class CuaRuntimeTests
{
    private static readonly CuaRuntimeOptions Fast = new() { SettleDelay = TimeSpan.Zero };

    [Fact]
    public async Task RunsPlannedCallsAndSendsHistoryBack()
    {
        var desktop = new FakeDesktop();
        var planner = new ScriptedPlanner(
            [Call("launch_app", new { app_name = "Notepad" })],
            [Call("click_element", new { element_index = 2 }), Call("type_text", new { text = "hi" })],
            [Call("finish", new { reason = "Typed hi." })]);

        var result = await new CuaRuntime(desktop, planner, Fast).RunAsync("type hi in notepad", default);

        Assert.Equal(CuaRunStatus.Done, result.Status);
        Assert.Equal("Typed hi.", result.Message);
        Assert.Equal(new[] { "launch_app", "click_element", "type_text" }, desktop.Executed);
        var last = planner.Requests[^1];
        Assert.Equal(3, last.History.Count);
        Assert.Contains("Verification: the window changed", last.History[^1].Message);
        Assert.Equal(3, last.Step);
    }

    [Fact]
    public async Task InvalidCallsGetRepairHintsThenFail()
    {
        var planner = new ScriptedPlanner(
            [Call("click_element", new { })],
            [Call("no_such_tool", new { })],
            [Call("set_value", new { element_index = 2 })]);

        var result = await new CuaRuntime(new FakeDesktop(), planner, Fast).RunAsync("x", default);

        Assert.Equal(CuaRunStatus.Failed, result.Status);
        Assert.Contains("click_element requires element_index", planner.Requests[1].History[0].Message);
        Assert.Contains("Unknown tool no_such_tool", planner.Requests[2].History[1].Message);
    }

    [Fact]
    public async Task InvalidCallRecoversWhenThePlannerFixesIt()
    {
        var planner = new ScriptedPlanner([Call("type_text", new { })], [Call("type_text", new { text = "a" })], [Call("finish", new { reason = "ok" })]);
        var result = await new CuaRuntime(new FakeDesktop(), planner, Fast).RunAsync("x", default);
        Assert.Equal(CuaRunStatus.Done, result.Status);
    }

    [Fact]
    public async Task RiskyClickStopsWithoutAConfirmationHandler()
    {
        var desktop = new FakeDesktop();
        var planner = new ScriptedPlanner([Call("click_element", new { element_index = 1, label = "button" })]);

        var result = await new CuaRuntime(desktop, planner, Fast).RunAsync("send it", default);

        Assert.Equal(CuaRunStatus.NeedsConfirmation, result.Status);
        Assert.Contains("\"Send\"", result.Message);
        Assert.Empty(desktop.Executed);
    }

    [Theory]
    [InlineData(true, CuaRunStatus.Done)]
    [InlineData(false, CuaRunStatus.Cancelled)]
    public async Task RiskyClickAsksTheUser(bool approve, CuaRunStatus expected)
    {
        var desktop = new FakeDesktop();
        var questions = new List<string>();
        var planner = new ScriptedPlanner([Call("click_element", new { element_index = 1 })], [Call("finish", new { reason = "Sent." })]);
        var options = Fast with { Confirm = (question, _) => { questions.Add(question); return Task.FromResult(approve); } };

        var result = await new CuaRuntime(desktop, planner, options).RunAsync("send it", default);

        Assert.Equal(expected, result.Status);
        Assert.Single(questions);
        Assert.Equal(approve ? new[] { "click_element" } : Array.Empty<string>(), desktop.Executed);
    }

    [Theory]
    [InlineData("f4", "alt", true)]
    [InlineData("w", "ctrl", true)]
    [InlineData("s", "ctrl", false)]
    [InlineData("enter", null, false)]
    public void ClosingShortcutsNeedConfirmation(string key, string? modifier, bool risky)
    {
        var call = Call("press_key", new { key, modifiers = modifier is null ? Array.Empty<string>() : [modifier] });
        Assert.Equal(risky, CuaRuntime.RiskReason(call, FakeDesktop.State(0)) is not null);
    }

    [Fact]
    public void ClickRiskUsesTheRealElementNameNotJustTheLabel()
    {
        var state = FakeDesktop.State(0);
        Assert.NotNull(CuaRuntime.RiskReason(Call("click_element", new { element_index = 1, label = "blue button" }), state));
        Assert.Null(CuaRuntime.RiskReason(Call("click_element", new { element_index = 2 }), state));
        Assert.NotNull(CuaRuntime.RiskReason(Call("click_point", new { screenshot_id = "x", x = 1, y = 1, label = "Delete draft" }), state));
        Assert.NotNull(CuaRuntime.RiskReason(Call("click_point", new { screenshot_id = "x", x = 1, y = 1, label = "Close Notepad window" }), state));
        Assert.NotNull(CuaRuntime.RiskReason(Call("click_point", new { screenshot_id = "x", x = 1, y = 1, label = "Don't save" }), state));
        Assert.Null(CuaRuntime.RiskReason(Call("click_point", new { screenshot_id = "x", x = 1, y = 1, label = "Save" }), state));
    }

    [Fact]
    public async Task AskUserPassesTheAnswerBackAndNoAnswerStops()
    {
        var ask = Call("ask_user", new { question = "Which Mom?", options = new[] { "Mom (mobile)", "Mom (work)" } });
        IReadOnlyList<string>? offered = null;
        var planner = new ScriptedPlanner([ask], [Call("finish", new { reason = "ok" })]);
        var answered = await new CuaRuntime(new FakeDesktop(), planner, Fast with
        {
            Ask = (_, options, _) => { offered = options; return Task.FromResult<string?>("Mom (mobile)"); },
        }).RunAsync("text mom", default);

        Assert.Equal(CuaRunStatus.Done, answered.Status);
        Assert.Equal(["Mom (mobile)", "Mom (work)"], offered);
        Assert.Contains(planner.Requests[1].History, outcome => outcome.Message == "The user answered: Mom (mobile)");

        var silent = await new CuaRuntime(new FakeDesktop(), new ScriptedPlanner([ask]), Fast with
        {
            Ask = (_, _, _) => Task.FromResult<string?>(null),
        }).RunAsync("text mom", default);
        Assert.Equal(CuaRunStatus.Cancelled, silent.Status);
    }

    [Fact]
    public void PillStatusNamesTheStep()
    {
        var state = FakeDesktop.State(0);
        Assert.Equal("Clicking Send", CuaRuntime.StatusFor(Call("click_element", new { element_index = 1, label = "x" }), state));
        Assert.Equal("Opening Spotify", CuaRuntime.StatusFor(Call("launch_app", new { app_name = "Spotify" }), state));
        Assert.Equal("Pressing ctrl+l", CuaRuntime.StatusFor(Call("press_key", new { key = "l", modifiers = new[] { "ctrl" } }), state));
        Assert.Equal("Typing in Text editor", CuaRuntime.StatusFor(Call("type_text", new { text = "secret", element_index = 2 }), state));
    }

    [Fact]
    public void EnterInAChatComposerNeedsConfirmation()
    {
        var notepad = FakeDesktop.State(0);
        var chat = notepad with { FocusedElement = new CuaFocusedElement("Edit", "Type a message", "", "", null, 1) };
        var search = notepad with { FocusedElement = new CuaFocusedElement("Edit", "Search or start a new chat", "", "", null, 1) };
        var enter = Call("press_key", new { key = "enter" });

        Assert.NotNull(CuaRuntime.RiskReason(enter, chat));
        Assert.Null(CuaRuntime.RiskReason(enter, notepad));
        Assert.Null(CuaRuntime.RiskReason(enter, search));
        Assert.NotNull(CuaRuntime.RiskReason(Call("type_text", new { text = "hi\n" }), chat));
        Assert.Null(CuaRuntime.RiskReason(Call("type_text", new { text = "hi" }), chat));
    }

    [Fact]
    public async Task RepeatingAnActionThatChangesNothingStops()
    {
        var desktop = new FakeDesktop { Frozen = true };
        var click = Call("click_element", new { element_index = 2 });
        var planner = new ScriptedPlanner([click], [click], [click]);

        var result = await new CuaRuntime(desktop, planner, Fast).RunAsync("x", default);

        Assert.Equal(CuaRunStatus.Failed, result.Status);
        Assert.Contains("changed nothing", result.Message);
        Assert.Equal(2, desktop.Executed.Count);
        Assert.Contains("nothing visible changed", planner.Requests[1].History[0].Message);
    }

    [Fact]
    public async Task BatchStopsAtTheFirstFailedCall()
    {
        var desktop = new FakeDesktop { FailTool = "click_element" };
        var planner = new ScriptedPlanner(
            [Call("click_element", new { element_index = 2 }), Call("type_text", new { text = "a" })],
            [Call("fail", new { reason = "gave up" })]);

        var result = await new CuaRuntime(desktop, planner, Fast).RunAsync("x", default);

        Assert.Equal(CuaRunStatus.Failed, result.Status);
        Assert.Equal(new[] { "click_element" }, desktop.Executed);
        Assert.Equal(CuaResultStatus.Failed, planner.Requests[1].History[0].Status);
    }

    [Fact]
    public async Task ThreeFailedStepsInARowStop()
    {
        var desktop = new FakeDesktop { FailTool = "type_text" };
        var type = Call("type_text", new { text = "a" });
        var result = await new CuaRuntime(desktop, new ScriptedPlanner([type], [type], [type], [type]), Fast).RunAsync("x", default);
        Assert.Equal(CuaRunStatus.Failed, result.Status);
        Assert.Equal(3, desktop.Executed.Count);
    }

    [Theory]
    [InlineData("I could not open Notepad.", true)]
    [InlineData("Task is incomplete", true)]
    [InlineData("Opened Notepad and typed the list.", false)]
    public void FinishThatReportsFailureIsAFailure(string reason, bool failed) =>
        Assert.Equal(failed, CuaRuntime.FinishLooksFailed(reason));

    [Fact]
    public async Task WaitForPollsUntilTextAppears()
    {
        var desktop = new FakeDesktop { TitleAfterObservations = (3, "Saved") };
        var planner = new ScriptedPlanner([Call("wait_for", new { text = "saved", timeout_seconds = 5 })], [Call("finish", new { reason = "ok" })]);

        var result = await new CuaRuntime(desktop, planner, Fast).RunAsync("x", default);

        Assert.Equal(CuaRunStatus.Done, result.Status);
        Assert.Equal(CuaResultStatus.Executed, planner.Requests[1].History[0].Status);
    }

    [Fact]
    public async Task StepLimitAndCancellationEndTheRun()
    {
        var type = Call("type_text", new { text = "a" });
        var limited = await new CuaRuntime(new FakeDesktop(), new ScriptedPlanner([type], [type], [type]), Fast with { MaxSteps = 2 }).RunAsync("x", default);
        Assert.Equal(CuaRunStatus.Failed, limited.Status);

        using var cancel = new CancellationTokenSource();
        var planner = new ScriptedPlanner([type]) { OnPlan = cancel.Cancel };
        var cancelled = await new CuaRuntime(new FakeDesktop(), planner, Fast).RunAsync("x", cancel.Token);
        Assert.Equal(CuaRunStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task VoiceCommandChangesAMuesliSetting()
    {
        var saved = new MuesliSettings { Theme = "dark", SoundEnabled = true };
        var settings = new CuaMuesliSettings(() => saved, value => saved = value);
        var planner = new ScriptedPlanner(
            [Call("get_muesli_settings", new { })],
            [Call("set_muesli_setting", new { setting = "theme", value = "Light" }), Call("set_muesli_setting", new { setting = "sounds", value = "off" })],
            [Call("finish", new { reason = "Done." })]);

        var result = await new CuaRuntime(new FakeDesktop(), planner, Fast with { Settings = settings }).RunAsync("light theme, no sounds", default);

        Assert.Equal(CuaRunStatus.Done, result.Status);
        Assert.Equal("light", saved.Theme);
        Assert.False(saved.SoundEnabled);
        Assert.Contains("theme (App theme): dark", planner.Requests[1].History[0].Message);
    }

    [Fact]
    public void SettingsRefuseUnknownSettingsAndMissingModels()
    {
        var saved = new MuesliSettings();
        var settings = new CuaMuesliSettings(() => saved, value => saved = value);
        Assert.False(settings.Execute(Call("set_muesli_setting", new { setting = "openai_api_key", value = "x" })).Succeeded);
        Assert.False(settings.Execute(Call("set_muesli_setting", new { setting = "theme", value = "purple" })).Succeeded);
        var missing = TranscriptionModelCatalog.Models.FirstOrDefault(model => !model.IsCached);
        if (missing is not null)
        {
            var result = settings.Execute(Call("set_muesli_setting", new { setting = "dictation_model", value = missing.Id }));
            Assert.Contains("not downloaded", result.Message);
            Assert.NotEqual(missing.Id, saved.DictationModelId);
        }
    }

    [Fact]
    public void CatalogIsWellFormed()
    {
        var tools = CuaCatalog.ResponsesTools();
        Assert.Equal(CuaCatalog.Tools.Count, tools.Count);
        Assert.Equal(CuaCatalog.Tools.Count, CuaCatalog.Tools.Select(tool => tool.Name).Distinct().Count());
        foreach (var tool in CuaCatalog.Tools)
        foreach (var key in tool.Required)
            Assert.NotNull(tool.Parameters["properties"]![key]);
        Assert.Equal("press_key", CuaCatalog.Find("hotkey")!.Name);
        Assert.Null(CuaCatalog.Validate(Call("click", new { element_id = "e4" })));
        Assert.Contains("screenshot_id", CuaCatalog.Validate(Call("click", new { x = 1, y = 2, label = "a" })));
    }

    private static CuaToolCall Call(string name, object arguments) => CuaToolCall.Parse(name, JsonSerializer.Serialize(arguments));

    private sealed class ScriptedPlanner(params CuaToolCall[][] steps) : ICuaPlanner
    {
        public List<CuaPlannerRequest> Requests { get; } = [];
        public Action? OnPlan { get; init; }

        public Task<CuaPlan> PlanAsync(CuaPlannerRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OnPlan?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var calls = Requests.Count <= steps.Length ? steps[Requests.Count - 1] : [Call("fail", new { reason = "script ended" })];
            foreach (var call in calls)
                if (CuaCatalog.Find(call.Name) is null && call.Name != "no_such_tool") throw new InvalidOperationException(call.Name);
            return Task.FromResult(new CuaPlan(calls));
        }
    }

    private sealed class FakeDesktop : ICuaDesktop
    {
        private int _version;
        private int _observations;
        public List<string> Executed { get; } = [];
        public bool Frozen { get; init; }
        public string? FailTool { get; init; }
        public (int After, string Title)? TitleAfterObservations { get; init; }

        public static CuaWindowState State(int version, string title = "Untitled - Notepad") => new(
            CuaWindowState.NewStateId(), "Notepad", "Notepad", 1, title, new CuaRect(0, 0, 800, 600), null, null,
            new CuaFocusedElement("Document", "Text editor", "", $"v{version}", null, 1), null, null, null,
            [
                new CuaElement("e1", 1, "Button", "Send", "", "", "", true, new CuaRect(10, 10, 50, 20), "", ["click"], 1),
                new CuaElement("e2", 2, "Document", "Text editor", "", "", "", true, new CuaRect(0, 40, 800, 500), "", ["click", "set_value"], 1),
            ],
            DateTimeOffset.UtcNow);

        public CuaWindowState Observe(string? app, long? windowId, bool includeScreenshot)
        {
            _observations++;
            return TitleAfterObservations is { } change && _observations >= change.After ? State(_version, change.Title) : State(_version);
        }

        public CuaResult Execute(CuaToolCall call, CancellationToken cancellationToken)
        {
            Executed.Add(call.Name);
            if (call.Name == FailTool) return CuaResult.Fail($"{call.Name} failed.");
            if (!Frozen) _version++;
            return CuaResult.Ok($"{call.Name} done.");
        }

        public void Dispose() { }
    }
}
