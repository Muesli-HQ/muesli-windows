using System.Diagnostics;
using Microsoft.Win32;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Port of macOS CotypistQuietApps (2f10b23d). Apps are identified by executable name, the app id the
/// text service sends (macOS uses bundle IDs).
/// </summary>
public static class CotypistQuietApps
{
    public sealed record App(string Id, string Name, string? Path);

    /// <summary>Apps people usually keep Cotypist out of: password managers, and editors with their
    /// own completions. Windows counterparts of the macOS list.</summary>
    public static readonly string[] RecommendedIds =
        ["1password.exe", "bitwarden.exe", "keepassxc.exe", "code.exe", "cursor.exe", "zed.exe", "devenv.exe"];

    private static readonly Dictionary<string, App?> Installed = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<App> Recommended(IReadOnlyCollection<string> quiet) =>
        RecommendedIds.Where(id => !quiet.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Select(id => Running().FirstOrDefault(app => app.Id == id) ?? Lookup(id))
            .OfType<App>().ToList();

    /// <summary>Open apps with windows, other than Muesli itself.</summary>
    public static IReadOnlyList<App> Running(IReadOnlyCollection<string>? quiet = null)
    {
        var self = Environment.ProcessId;
        var apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || process.MainWindowHandle == IntPtr.Zero) continue;
                    var path = process.MainModule?.FileName;
                    if (path is null) continue;
                    var id = System.IO.Path.GetFileName(path).ToLowerInvariant();
                    if (quiet?.Contains(id, StringComparer.OrdinalIgnoreCase) == true || apps.ContainsKey(id)) continue;
                    apps[id] = new App(id, NameOf(path, id), path);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Elevated and protected processes can't be inspected; Cotypist can't run in them either.
                }
            }
        }
        return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A quiet app's display name: running, installed, or its executable name.</summary>
    public static App Describe(string id) =>
        Running().FirstOrDefault(app => app.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Lookup(id) ?? new App(id, id, null);

    /// <summary>Registered under App Paths: Windows' list of installed apps launchable by exe name.</summary>
    private static App? Lookup(string id)
    {
        lock (Installed)
        {
            if (Installed.TryGetValue(id, out var cached)) return cached;
            string? path = null;
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{id}");
                if (key?.GetValue(null) is string value && File.Exists(value.Trim('"'))) { path = value.Trim('"'); break; }
            }
            return Installed[id] = path is null ? null : new App(id, NameOf(path, id), path);
        }
    }

    private static string NameOf(string path, string id)
    {
        var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        return string.IsNullOrWhiteSpace(description) ? System.IO.Path.GetFileNameWithoutExtension(id) : description.Trim();
    }
}
