using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Dictionary correction suggestions, end to end: starts the correction monitor after each pasted
/// dictation (only while the setting is on), records what it finds, and shows one "Add correction?"
/// card at a time beside the pill. The queueing is macOS <c>MuesliController</c>'s: up to ten waiting,
/// 150 ms between cards, a correction answered meanwhile is skipped.
/// </summary>
public sealed class DictionarySuggestionPrompts : IDisposable
{
    private const int MaxQueuedPrompts = 10;
    private static readonly TimeSpan PromptAdvanceDelay = TimeSpan.FromMilliseconds(150);

    private readonly WinUiDictationContext _dictation;
    private readonly WinUiSettingsContext _settings;
    private readonly WinUiLibraryContext _library;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<global::Windows.Graphics.RectInt32?> _indicatorFrame;
    private readonly AppLogService _log = new();
    private readonly DictationCorrectionMonitor _monitor;
    private readonly List<string> _queue = [];
    private DictionarySuggestionPromptWindow? _prompt;
    private string? _activeKey;
    private bool _advancePending;
    private bool _disposed;

    public DictionarySuggestionPrompts(
        WinUiDictationContext dictation,
        WinUiSettingsContext settings,
        WinUiLibraryContext library,
        IUiDispatcher dispatcher,
        Func<global::Windows.Graphics.RectInt32?> indicatorFrame)
    {
        _dictation = dictation;
        _settings = settings;
        _library = library;
        _dispatcher = dispatcher;
        _indicatorFrame = indicatorFrame;
        _monitor = new DictationCorrectionMonitor(_log);
        _dictation.DictationStarting += OnDictationStarting;
        _dictation.DictationPasted += OnDictationPasted;
        _settings.Changed += OnSettingsChanged;
    }

    private void OnDictationStarting() => _monitor.Cancel();

    private void OnDictationPasted(string text, IntPtr target)
    {
        if (_disposed || !_settings.Load().EnableDictionaryCorrectionPrompts) return;
        _monitor.Start(text, target, suggestion => _dispatcher.TryEnqueue(() => Record(suggestion)));
    }

    /// <summary>Turning the setting off stops watching and closes any card, as on macOS.</summary>
    private void OnSettingsChanged(object? sender, MuesliSettings settings)
    {
        if (settings.EnableDictionaryCorrectionPrompts) return;
        _monitor.Cancel();
        _queue.Clear();
        _activeKey = null;
        _prompt?.CloseQuietly();
        _prompt = null;
    }

    private void Record(DictionarySuggestion suggestion)
    {
        if (_disposed) return;
        try
        {
            var (updated, prompt) = DictionarySuggestionLedger.Add(_settings.Load(), suggestion, _library.LoadDictionary());
            if (updated is not null) _settings.Save(updated);
            if (prompt is not null) Enqueue(prompt);
        }
        catch (Exception exception)
        {
            // This runs as a bare dispatcher callback: an IOException from a full disk or a locked
            // settings file would otherwise reach UiExceptionPolicy, which treats it as fatal. The
            // feature is optional, so a failed save only loses this one suggestion.
            _log.Error("Dictionary suggestion could not be recorded.", exception);
        }
    }

    private void Enqueue(DictionarySuggestion suggestion)
    {
        var key = DictionarySuggestionLedger.Key(suggestion.Observed, suggestion.Replacement);
        if (key == _activeKey || _queue.Contains(key)) return;
        _queue.Add(key);
        if (_queue.Count > MaxQueuedPrompts) _queue.RemoveRange(0, _queue.Count - MaxQueuedPrompts);
        PresentNext();
    }

    private void PresentNext()
    {
        if (_disposed || _activeKey is not null || _advancePending) return;
        var settings = _settings.Load();
        if (!settings.EnableDictionaryCorrectionPrompts) return;
        var dictionary = _library.LoadDictionary();
        while (_queue.Count > 0)
        {
            var key = _queue[0];
            _queue.RemoveAt(0);
            if ((settings.DismissedDictionarySuggestionKeys ?? []).Contains(key)) continue;
            if (dictionary.Any(entry => DictionarySuggestionLedger.Key(entry.Phrase, entry.Replacement) == key)) continue;
            var suggestion = (settings.DictionarySuggestions ?? []).FirstOrDefault(item => DictionarySuggestionLedger.Key(item.Observed, item.Replacement) == key);
            if (suggestion is null) continue;
            Present(suggestion, key);
            return;
        }
    }

    private void Present(DictionarySuggestion suggestion, string key)
    {
        _activeKey = key;
        _log.Info($"Dictionary suggestion shown. observedChars={suggestion.Observed.Length}; replacementChars={suggestion.Replacement.Length}");
        try
        {
            _prompt = new DictionarySuggestionPromptWindow(
                suggestion,
                _indicatorFrame(),
                onAdd: () => Complete(key, "add", () => Accept(suggestion)),
                onIgnore: () => Complete(key, "ignore", () => _settings.Save(DictionarySuggestionLedger.Ignore(_settings.Load(), suggestion))),
                onDismiss: () => Complete(key, "dismiss", null));
            _prompt.ShowPrompt();
        }
        catch (Exception exception)
        {
            // The card is optional; the suggestion stays on the Dictionary page.
            _log.Error("Dictionary suggestion card could not be shown.", exception);
            _prompt = null;
            _activeKey = null;
        }
    }

    private void Accept(DictionarySuggestion suggestion)
    {
        var (settings, dictionary) = DictionarySuggestionLedger.Accept(_settings.Load(), suggestion, _library.LoadDictionary());
        if (dictionary is not null) _library.SaveDictionary(dictionary);
        _settings.Save(settings);
    }

    private void Complete(string key, string action, Action? apply)
    {
        if (_activeKey != key) return;
        try
        {
            apply?.Invoke();
        }
        catch (Exception exception)
        {
            _log.Error($"Dictionary suggestion {action} could not be saved.", exception);
        }

        _log.Info($"Dictionary suggestion answered. action={action}; queued={_queue.Count}");
        _activeKey = null;
        _prompt = null;
        _advancePending = true;
        _ = Task.Delay(PromptAdvanceDelay).ContinueWith(_ => _dispatcher.TryEnqueue(() =>
        {
            _advancePending = false;
            try
            {
                PresentNext();
            }
            catch (Exception exception)
            {
                // Same containment as Record: reading settings or the dictionary must not end the app.
                _log.Error("Next dictionary suggestion could not be shown.", exception);
            }
        }), TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dictation.DictationStarting -= OnDictationStarting;
        _dictation.DictationPasted -= OnDictationPasted;
        _settings.Changed -= OnSettingsChanged;
        _monitor.Dispose();
        _prompt?.CloseQuietly();
        _prompt = null;
    }
}
