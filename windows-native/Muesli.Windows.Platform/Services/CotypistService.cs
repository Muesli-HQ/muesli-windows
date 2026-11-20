using System.Diagnostics;
using System.Text.Json.Nodes;
using Muesli.Windows.Cotypist;

namespace Muesli.Windows.Services;

/// <summary>
/// Port of macOS CotypistService request handling (2f10b23d). The TSF helper owns keys, ghost text
/// and insertion; it asks this service for each suggestion over <see cref="CotypistPipeServer"/>.
/// Muesli picks the context, runs the base model, gates the result and records every outcome.
/// Started only when <see cref="CotypistAvailability.IsHelperBundled"/>.
/// </summary>
public sealed class CotypistService : IDisposable
{
    private readonly Func<MuesliSettings> _settings;
    private readonly CotypistHistoryStore _history;
    private readonly Action<string> _log;
    private readonly CotypistRequestLedger _requests = new();
    private readonly Lock _gate = new();
    private readonly List<(string Id, string Before, string Model)> _recent = [];
    private Task<CotypistEngine>? _engine;
    private string? _engineModel;

    /// <summary>Called after an accepted suggestion is recorded, to refresh the timeline.</summary>
    public event EventHandler? Accepted;

    public CotypistService(Func<MuesliSettings> settings, CotypistHistoryStore history, Action<string> log)
    {
        _settings = settings;
        _history = history;
        _log = log;
    }

    public async Task<JsonObject> HandleAsync(JsonObject request)
    {
        switch ((string?)request["type"])
        {
            case "suggest":
                var settings = _settings();
                // Every reply carries the settings the helper applies itself.
                var reply = await SuggestAsync(request, settings).ConfigureAwait(false);
                reply["midWordDelay"] = settings.CotypistCompleteWords ? CotypistOptions.Parse<CotypistWordPause>(settings.CotypistWordPause).Seconds() : 0;
                return reply;
            case "outcome":
                Record(request);
                return [];
            default:
                return new JsonObject { ["rejection"] = "unknown request" };
        }
    }

    private async Task<JsonObject> SuggestAsync(JsonObject request, MuesliSettings settings)
    {
        var app = (string?)request["app"] ?? "";
        var midWord = (bool?)request["midWord"] ?? false;
        var afterSpace = (bool?)request["afterSpace"] ?? false;
        var firstWordOnly = (bool?)request["firstWordOnly"] ?? false;
        var isCurrent = _requests.Note((string?)request["client"], (int?)request["generation"]);
        if (settings.CotypistDisabledApps.Contains(app, StringComparer.OrdinalIgnoreCase)) return Reject("off in this app");
        if ((bool?)request["caretAtStart"] == true) return Reject("caret at start");
        var model = CotypistModels.Get(settings.CotypistModel);
        if (!model.IsDownloaded) return Reject("model not downloaded");
        if (midWord && !settings.CotypistCompleteWords) return Reject("completing words is off");
        var frequency = CotypistOptions.Parse<CotypistFrequency>(settings.CotypistFrequency);
        var thresholds = new CotypistShaper.Thresholds(CotypistOptions.FirstWordGate(frequency, midWord),
            MaxWords: CotypistOptions.Parse<CotypistSuggestionLength>(settings.CotypistSuggestionLength).MaxWords());

        // ponytail: field text or the helper's keystrokes only; UIA document context and screen OCR come
        // with the TSF helper, which is what can report the focused document.
        var field = (string?)request["field"];
        var (text, source) = !string.IsNullOrEmpty(field) ? (field, "field") : ((string?)request["typed"] ?? "", "keystrokes");
        if (CotypistAgentPrompt.Context(text) is { } agentPrompt) (text, source) = (agentPrompt, "agent-prompt");
        if (text.Length == 0) return Reject("no context", source);
        var before = text;
        string? heal = null;
        if (midWord)
        {
            if (CotypistText.SplitPartialWord(text) is not { } split) return Reject("mid-word: no partial word");
            (text, heal) = split;
        }
        var prompt = text.Trim(' ');
        if (prompt.Length < 3) return Reject("too little context", source);

        try
        {
            var engine = await LoadEngineAsync(model).ConfigureAwait(false);
            var maxTokens = firstWordOnly ? 4 : 10;
            var completion = await engine.CompleteAsync(prompt, maxTokens, heal, isCurrent).ConfigureAwait(false);
            var shaped = CotypistShaper.Shape(completion, afterSpace, heal, firstWordOnly, thresholds);
            var reply = new JsonObject { ["source"] = source, ["modelMs"] = (int)(completion.PromptMs + completion.DecodeMs) };
            if (!shaped.IsShown)
            {
                reply["rejection"] = shaped.Rejection.ToString();
                // The first word ran past the first stage's budget; the full request may finish it.
                reply["retryFull"] = shaped.Rejection == CotypistRejection.FirstWordIncomplete;
                return reply;
            }
            var id = Guid.NewGuid().ToString();
            Remember(id, before, model.Id);
            reply["text"] = shaped.Text;
            reply["firstWordLogProb"] = shaped.FirstWordLogProb;
            // With one-word suggestions the first stage is the whole suggestion.
            reply["complete"] = firstWordOnly && thresholds.MaxWords == 1;
            reply["id"] = id;
            return reply;
        }
        catch (OperationCanceledException)
        {
            return Reject("superseded", source);
        }
        catch (Exception error)
        {
            return Reject($"engine: {error.GetType().Name}", source);
        }
    }

    private static JsonObject Reject(string reason, string? source = null)
    {
        var reply = new JsonObject { ["rejection"] = reason };
        if (source is not null) reply["source"] = source;
        return reply;
    }

    private Task<CotypistEngine> LoadEngineAsync(CotypistModel model)
    {
        lock (_gate)
        {
            if (_engine is not null && _engineModel == model.Id) return _engine;
            Unload();
            _engineModel = model.Id;
            return _engine = Task.Run(async () =>
            {
                var started = Stopwatch.StartNew();
                var engine = new CotypistEngine(model.Path!);
                // Warm the healing vocabulary so the first real suggestion isn't the slow one.
                await engine.CompleteAsync("Thanks for the", 2, " qui").ConfigureAwait(false);
                _log($"Cotypist engine ready ({model.Id}) in {started.Elapsed.TotalSeconds:0.0}s");
                return engine;
            });
        }
    }

    /// <summary>After a model is picked while Cotypist is on: load it now, not on the next keystroke.</summary>
    public void ModelChanged()
    {
        var model = CotypistModels.Get(_settings().CotypistModel);
        if (model.IsDownloaded) _ = LoadEngineAsync(model);
    }

    /// <summary>The model holds hundreds of megabytes; turning Cotypist off frees it.</summary>
    public void Unload()
    {
        lock (_gate)
        {
            if (_engine is null) return;
            var engine = _engine;
            _engine = null;
            _engineModel = null;
            _ = engine.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); }, TaskScheduler.Default);
            _log("Cotypist engine released");
        }
    }

    private void Remember(string id, string before, string model)
    {
        lock (_gate)
        {
            _recent.Add((id, before, model));
            if (_recent.Count > 32) _recent.RemoveRange(0, _recent.Count - 32);
        }
    }

    private void Record(JsonObject message)
    {
        var id = (string?)message["id"];
        (string Id, string Before, string Model) suggested;
        lock (_gate) suggested = _recent.LastOrDefault(entry => entry.Id == id);
        var app = (string?)message["app"] ?? "";
        if (CotypistMessages.Event(message, CotypistMessages.Sentence(suggested.Before ?? ""), suggested.Model ?? _settings().CotypistModel,
                AppName(app), DateTimeOffset.UtcNow) is not { } outcome) return;
        _ = Task.Run(() =>
        {
            try
            {
                _history.Record(outcome);
                // Log lines name the app only for an accepted suggestion.
                if (outcome.AcceptedText.Length > 0) Accepted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception error)
            {
                _log($"Cotypist could not record a suggestion. category={error.GetType().Name}");
            }
        });
    }

    /// <summary>The app's name as the user knows it; empty, never the process name, when unknown.</summary>
    private static string AppName(string processName)
    {
        try
        {
            using var process = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName)).FirstOrDefault();
            return process?.MainModule?.FileVersionInfo.FileDescription ?? "";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    public void Dispose() => Unload();
}
