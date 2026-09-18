using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Muesli.Windows.Services;

public sealed class TrayIconService : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;
    private Window? _window;
    private Func<ProductExperienceState>? _snapshot;
    private Action<string>? _navigate;

    public void Initialize(Window window, Func<ProductExperienceState>? snapshot = null, Action<string>? navigate = null)
    {
        _window = window;
        _snapshot = snapshot;
        _navigate = navigate;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Muesli",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    public void Refresh()
    {
        if (_notifyIcon is null) return;
        var replacement = BuildContextMenu();
        var previous = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = replacement;
        previous?.Dispose();
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window is MainWindow mainWindow)
        {
            mainWindow.ShowDashboardFromBackground();
            return;
        }

        _window.ShowInTaskbar = true;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }

    private static Icon LoadTrayIcon()
    {
        return AppShellIcon.CreateNotifyIcon() ?? SystemIcons.Application;
    }

    private Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Muesli", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(ShowWindow));
        ProductExperienceState? state = null;
        Exception? snapshotFailure = null;
        try { state = _snapshot?.Invoke(); }
        catch (Exception exception) { snapshotFailure = exception; }
        if (snapshotFailure is not null)
        {
            var unavailable = menu.Items.Add("Muesli status unavailable; refresh again shortly");
            unavailable.Enabled = false;
        }
        if (state?.SetupIncomplete == true) menu.Items.Add("Resume setup", null, (_, _) => Invoke("resume"));
        menu.Items.Add("Feature tour", null, (_, _) => Invoke("tour"));
        AddHistory(menu, "Recent dictations", state?.RecentDictations, "dictations");
        AddHistory(menu, "Recent meetings", state?.RecentMeetings, "meetings");
        menu.Items.Add($"Detected now: {state?.DetectedNow ?? "No meeting detected"}", null, (_, _) => Invoke("meetings"));
        if (state?.MeetingRecording == true)
        {
            menu.Items.Add(state.MeetingPaused ? "Resume meeting" : "Pause meeting", null, (_, _) => Invoke("pause-meeting"));
            menu.Items.Add("Stop meeting", null, (_, _) => Invoke("stop-meeting"));
            menu.Items.Add("Discard meeting", null, (_, _) => Invoke("discard-meeting"));
        }
        var upcoming = menu.Items.Add("Upcoming meetings: unavailable (no calendar source)"); upcoming.Enabled = false;
        menu.Items.Add("Settings", null, (_, _) => Invoke("settings"));
        menu.Items.Add("About and diagnostics", null, (_, _) => Invoke("about"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(System.Windows.Application.Current.Shutdown));
        return menu;
    }

    private void AddHistory(Forms.ContextMenuStrip menu, string title, IReadOnlyList<ProductHistoryEntry>? entries, string target)
    {
        var root = new Forms.ToolStripMenuItem(title);
        if (entries is null || entries.Count == 0) root.DropDownItems.Add("No recent items").Enabled = false;
        else foreach (var entry in entries.Take(6)) root.DropDownItems.Add($"{entry.Timestamp.LocalDateTime:g} · {entry.Title}", null, (_, _) => Invoke(target));
        menu.Items.Add(root);
    }
    private void Invoke(string destination) => System.Windows.Application.Current.Dispatcher.Invoke(() => { ShowWindow(); _navigate?.Invoke(destination); });
}
