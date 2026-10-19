using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// macOS dictionary suggestions end to end: after each paste (when the setting is on) the monitor
/// watches the field, each correction is recorded in settings, and an "Add correction?" prompt is
/// shown one at a time. Add stores the entry; Ignore never offers the pair again; closing or timing
/// out leaves it in the Dictionary page's list.
/// </summary>
public sealed class WinUiDictionarySuggestionService : IDisposable
{
    private const int MaxQueue = 10;

    private readonly WinUiLibraryContext _library;
    private readonly WinUiSettingsContext _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly WinUiDictationContext _dictation;
    private readonly DictationCorrectionMonitor _monitor = new();
    private readonly List<string> _queue = [];
    private MeetingNotificationWindow? _window;
    private string? _activeKey;
    private IntPtr _anchor;

    public WinUiDictionarySuggestionService(
        WinUiLibraryContext library, WinUiSettingsContext settings, IUiDispatcher dispatcher, WinUiDictationContext dictation)
    {
        _library = library;
        _settings = settings;
        _dispatcher = dispatcher;
        _dictation = dictation;
        _dictation.Pasted += OnPasted;
    }

    /// <summary>Turns the feature on or off; turning it off stops any watch in progress.</summary>
    public void SetEnabled(bool enabled)
    {
        if (!enabled) _monitor.Cancel();
        _settings.Save(_settings.Load() with { EnableDictionaryCorrectionPrompts = enabled });
    }

    public void Accept(DictionarySuggestion suggestion)
    {
        var (settings, words) = DictionarySuggestionBook.Accept(_settings.Load(), _library.LoadDictionary(), suggestion);
        _library.SaveDictionary(words);
        _settings.Save(settings);
    }

    public void Ignore(DictionarySuggestion suggestion) =>
        _settings.Save(DictionarySuggestionBook.Dismiss(_settings.Load(), suggestion));

    private void OnPasted(DictationPaste paste)
    {
        if (!_settings.Load().EnableDictionaryCorrectionPrompts || paste.Window == IntPtr.Zero) return;
        var context = paste.App is { } app ? $"{app.Name}|{app.AppId}" : "";
        _monitor.Start(paste.Text, context, paste.Window, suggestion => _dispatcher.TryEnqueue(() => Record(suggestion, paste.Window)));
    }

    private void Record(DictionarySuggestion suggestion, IntPtr window)
    {
        var (settings, prompt) = DictionarySuggestionBook.Add(_settings.Load(), _library.LoadDictionary(), suggestion);
        if (prompt is null) return;
        _settings.Save(settings);
        if (_activeKey == prompt.Key || _queue.Contains(prompt.Key)) return;
        _queue.Add(prompt.Key);
        if (_queue.Count > MaxQueue) _queue.RemoveAt(0);
        _anchor = window;
        ShowNext();
    }

    private void ShowNext()
    {
        if (_window is not null) return;
        while (_queue.Count > 0)
        {
            var key = _queue[0];
            _queue.RemoveAt(0);
            if (DictionarySuggestionBook.Pending(_settings.Load(), _library.LoadDictionary(), key) is not { } suggestion) continue;
            Show(suggestion);
            return;
        }
    }

    private void Show(DictionarySuggestion suggestion)
    {
        _activeKey = suggestion.Key;
        var request = new MeetingNotificationRequest(
            $"dictionary:{suggestion.Key}",
            MeetingNotificationKind.DictionarySuggestion,
            "Add correction?",
            suggestion.Display,
            "Dictionary",
            "\U0001F4D6",
            "8E8E93",
            "",
            "Add",
            null,
            WindowHandle: _anchor,
            SecondaryActionLabel: "Ignore");
        MeetingNotificationWindow? window = null;
        window = new MeetingNotificationWindow(
            request,
            action =>
            {
                if (action == MeetingNotificationAction.Accept) Accept(suggestion);
                else if (action == MeetingNotificationAction.Ignore) Ignore(suggestion);
            },
            () => { },
            // A timed-out window can skip its close callback, so both paths release the slot.
            () => Finished(window),
            () => Finished(window));
        _window = window;
        window.ShowNotification();
    }

    private void Finished(MeetingNotificationWindow? window)
    {
        if (!ReferenceEquals(window, _window)) return;
        _window = null;
        _activeKey = null;
        _dispatcher.TryEnqueue(ShowNext);
    }

    public void Dispose()
    {
        _dictation.Pasted -= OnPasted;
        _monitor.Dispose();
        _window?.SuppressCallbacks();
    }
}
