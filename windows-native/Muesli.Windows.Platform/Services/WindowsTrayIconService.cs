using System.Drawing;
using Muesli.Windows.Core.Contracts;
using Forms = System.Windows.Forms;

namespace Muesli.Windows.Services;

/// <summary>Windows notification-area adapter with framework-neutral callbacks.</summary>
public sealed class WindowsTrayIconService : IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<bool> _dictationRecording;
    private readonly Func<bool> _meetingRecording;
    private readonly Func<Task> _toggleDictation;
    private readonly Func<Task> _toggleMeeting;
    private readonly Action<string> _show;
    private readonly Action _quit;
    private readonly Func<bool>? _meetingPaused;
    private readonly Func<Task>? _pauseMeeting;
    private readonly Func<Task>? _resumeMeeting;
    private readonly Action? _resumeSetup;
    private readonly Action? _featureTour;
    private readonly Forms.NotifyIcon _icon;
    private int _disposed;

    public WindowsTrayIconService(
        IUiDispatcher dispatcher,
        Func<bool> dictationRecording,
        Func<bool> meetingRecording,
        Func<Task> toggleDictation,
        Func<Task> toggleMeeting,
        Action<string> show,
        Action quit,
        string? iconPath = null,
        Func<bool>? meetingPaused = null,
        Func<Task>? pauseMeeting = null,
        Func<Task>? resumeMeeting = null,
        Action? resumeSetup = null,
        Action? featureTour = null)
    {
        _dispatcher = dispatcher;
        _dictationRecording = dictationRecording;
        _meetingRecording = meetingRecording;
        _toggleDictation = toggleDictation;
        _toggleMeeting = toggleMeeting;
        _show = show;
        _quit = quit;
        _meetingPaused = meetingPaused;
        _pauseMeeting = pauseMeeting;
        _resumeMeeting = resumeMeeting;
        _resumeSetup = resumeSetup;
        _featureTour = featureTour;
        _icon = new Forms.NotifyIcon
        {
            Text = "Muesli",
            Icon = LoadIcon(iconPath),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _icon.DoubleClick += (_, _) => Show("timeline");
    }

    public void Refresh() => _dispatcher.TryEnqueue(() =>
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var replacement = BuildMenu();
        var prior = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = replacement;
        prior?.Dispose();
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Muesli", null, (_, _) => Show("timeline"));
        menu.Items.Add(_dictationRecording() ? "Stop dictation" : "Start dictation", null, (_, _) =>
            _dispatcher.TryEnqueue(() => _ = _toggleDictation()));
        if (_meetingPaused?.Invoke() == true)
        {
            menu.Items.Add("Resume meeting", null, (_, _) =>
                _dispatcher.TryEnqueue(() => _ = _resumeMeeting?.Invoke() ?? Task.CompletedTask));
            menu.Items.Add("Stop and save meeting", null, (_, _) =>
                _dispatcher.TryEnqueue(() => _ = _toggleMeeting()));
        }
        else if (_meetingRecording())
        {
            menu.Items.Add("Pause meeting", null, (_, _) =>
                _dispatcher.TryEnqueue(() => _ = _pauseMeeting?.Invoke() ?? Task.CompletedTask));
            menu.Items.Add("Stop and save meeting", null, (_, _) =>
                _dispatcher.TryEnqueue(() => _ = _toggleMeeting()));
        }
        else
        {
            menu.Items.Add("Start Quick Note", null, (_, _) =>
                _dispatcher.TryEnqueue(() => _ = _toggleMeeting()));
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Dictations", null, (_, _) => Show("dictations"));
        menu.Items.Add("Meetings", null, (_, _) => Show("meetings"));
        menu.Items.Add("Settings", null, (_, _) => Show("settings"));
        menu.Items.Add("About and diagnostics", null, (_, _) => Show("about"));
        if (_resumeSetup is not null)
        {
            menu.Items.Add("Resume setup", null, (_, _) => _dispatcher.TryEnqueue(_resumeSetup));
        }
        if (_featureTour is not null)
        {
            menu.Items.Add("Replay feature tour", null, (_, _) => _dispatcher.TryEnqueue(_featureTour));
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _dispatcher.TryEnqueue(_quit));
        return menu;
    }

    private void Show(string destination) => _dispatcher.TryEnqueue(() => _show(destination));

    private static Icon LoadIcon(string? iconPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath)) return new Icon(iconPath);
        }
        catch { }
        return SystemIcons.Application;
    }
}
