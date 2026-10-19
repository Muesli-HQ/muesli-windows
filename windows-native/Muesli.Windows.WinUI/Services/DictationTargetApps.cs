using System.Diagnostics;
using System.Text.Json;
using Muesli.Windows.ComputerUse;

namespace Muesli.Windows.WinUI.Services;

/// <summary>The app a dictation was pasted into (macOS <c>targetAppName</c> / <c>targetAppBundleID</c>).</summary>
public sealed record DictationTargetApp(string Name, string AppId, string? ExePath);

/// <summary>
/// Where each dictation was pasted, kept in <c>dictation-apps.json</c> beside the history (id → app)
/// so the history schema and older profiles stay unchanged. Rows go with their dictation.
/// </summary>
public static class DictationTargetApps
{
    private static readonly object Gate = new();

    /// <summary>Same app: compared by executable id, as macOS compares bundle ids.</summary>
    public static bool Same(DictationTargetApp? left, DictationTargetApp? right) =>
        left is not null && right is not null && string.Equals(left.AppId, right.AppId, StringComparison.OrdinalIgnoreCase);

    /// <summary>One entry per app (the first seen, so newest-first input keeps the newest name), by name.</summary>
    public static IReadOnlyList<DictationTargetApp> Distinct(IEnumerable<DictationTargetApp?> apps) =>
        apps.OfType<DictationTargetApp>()
            .GroupBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static string PathFor(string profileRoot) => Path.Combine(profileRoot, "dictation-apps.json");

    /// <summary>The app that owns <paramref name="window"/>, or null when it cannot be read.</summary>
    public static DictationTargetApp? Capture(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        var (processId, appId, name) = Win32Desktop.Identity(window);
        if (processId == 0 || processId == Environment.ProcessId || string.IsNullOrWhiteSpace(name)) return null;
        string? exePath = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            exePath = process.MainModule?.FileName;
        }
        catch { /* elevated or exited: the name still shows, with the generic icon */ }
        return new DictationTargetApp(name, appId, exePath);
    }

    public static IReadOnlyDictionary<string, DictationTargetApp> LoadAll(string profileRoot)
    {
        lock (Gate) return Read(profileRoot);
    }

    public static void Record(string profileRoot, string dictationId, DictationTargetApp app)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, DictationTargetApp>(Read(profileRoot)) { [dictationId] = app };
            Write(profileRoot, all);
        }
    }

    public static void Remove(string profileRoot, string dictationId)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, DictationTargetApp>(Read(profileRoot));
            try { if (all.Remove(dictationId)) Write(profileRoot, all); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Clear(string profileRoot)
    {
        lock (Gate)
        {
            try { File.Delete(PathFor(profileRoot)); }
            catch (IOException) { }
        }
    }

    private static Dictionary<string, DictationTargetApp> Read(string profileRoot)
    {
        try
        {
            var path = PathFor(profileRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DictationTargetApp>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Write(string profileRoot, Dictionary<string, DictationTargetApp> all)
    {
        var path = PathFor(profileRoot);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all));
        File.Move(temporary, path, overwrite: true);
    }
}
