using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS <c>DictationCorrectionMonitor</c> on UI Automation. For 45 s after a paste it reads the text
/// of the focused field in the app that received it (and that app's window, within a node budget),
/// waits for each edit to settle, and reports word corrections. Text is held in memory only; just the
/// detected word pair leaves this class. Password fields and other apps are never read.
/// </summary>
public sealed class DictationCorrectionMonitor : IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SteadyInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FastWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MonitoringWindow = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(1.5);
    private const int MaxSuggestionsPerSession = 3;
    private const int MaxNodes = 140;
    private const int MaxDepth = 8;
    private const int MaxCharacters = 2_000;

    private readonly AppLogService _log = new();
    private CancellationTokenSource? _session;

    public void Start(string originalText, string appContext, IntPtr targetWindow, Action<DictionarySuggestion> onSuggestion)
    {
        Cancel();
        if (string.IsNullOrWhiteSpace(originalText) || targetWindow == IntPtr.Zero) return;
        var session = _session = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(originalText.Trim(), appContext, targetWindow, onSuggestion, session.Token));
    }

    public void Cancel()
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.Cancel();
    }

    public void Dispose() => Cancel();

    private async Task RunAsync(string original, string appContext, IntPtr window, Action<DictionarySuggestion> onSuggestion, CancellationToken token)
    {
        try
        {
            using var automation = new UIA3Automation();
            var processId = ProcessIdOf(window);
            if (processId == 0) return;
            var started = DateTime.UtcNow;
            var stabilizer = new DictationCorrectionSnapshotStabilizer();
            var emitted = new HashSet<string>();
            await Task.Delay(InitialDelay, token);
            while (!token.IsCancellationRequested && DateTime.UtcNow - started < MonitoringWindow)
            {
                var snapshots = Snapshots(automation, window, processId, original);
                foreach (var stable in stabilizer.Observe(snapshots, DateTime.UtcNow, QuietWindow))
                {
                    var english = WindowsSpellChecker.Recognized(DictionaryCorrectionDetector.RequiredEnglishWordCandidates(original, stable));
                    foreach (var suggestion in DictionaryCorrectionDetector.Suggestions(original, stable, appContext, MaxSuggestionsPerSession, english))
                    {
                        if (token.IsCancellationRequested || !emitted.Add(suggestion.Key)) continue;
                        onSuggestion(suggestion);
                        if (emitted.Count >= MaxSuggestionsPerSession) return;
                    }
                }
                await Task.Delay(DateTime.UtcNow - started < FastWindow ? FastInterval : SteadyInterval, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.Error("Dictionary correction monitoring stopped.", exception);
        }
    }

    private static List<string> Snapshots(UIA3Automation automation, IntPtr window, int processId, string original)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new List<string>();
        void Add(string? value)
        {
            if (DictationCorrectionSnapshotStabilizer.Normalize(value, original, MaxCharacters) is { } snapshot &&
                DictionaryCorrectionDetector.HasSufficientSharedContext(original, snapshot) &&
                seen.Add(snapshot))
            {
                snapshots.Add(snapshot);
            }
        }

        try
        {
            var focused = automation.FocusedElement();
            if (focused is not null && Safe(() => focused.Properties.ProcessId.ValueOrDefault) == processId) Add(TextOf(focused));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException) { }

        try
        {
            var root = automation.FromHandle(window);
            var budget = MaxNodes;
            Walk(root, 0, ref budget, Add);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException) { }
        return snapshots;
    }

    private static void Walk(AutomationElement element, int depth, ref int budget, Action<string?> add)
    {
        if (depth > MaxDepth || budget <= 0) return;
        budget--;
        var type = Safe(() => element.Properties.ControlType.ValueOrDefault);
        if (type is ControlType.Edit or ControlType.Document) add(TextOf(element));
        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException) { return; }
        foreach (var child in children)
        {
            Walk(child, depth + 1, ref budget, add);
            if (budget <= 0) return;
        }
    }

    /// <summary>The field's text: its value, else its document text, never a password.</summary>
    private static string? TextOf(AutomationElement element)
    {
        if (Safe(() => element.Properties.IsPassword.ValueOrDefault)) return null;
        var value = Safe(() => element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault);
        if (!string.IsNullOrEmpty(value)) return value.Length <= MaxCharacters ? value : null;
        var text = Safe(() => element.Patterns.Text.PatternOrDefault?.DocumentRange.GetText(MaxCharacters + 1));
        return text is { Length: > 0 and <= MaxCharacters } ? text : null;
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException) { return default; }
    }

    private static int ProcessIdOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return (int)processId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

/// <summary>
/// macOS checks changed words with NSSpellChecker; Windows uses the system spell checker (ISpellChecker).
/// Unavailable checkers simply recognise nothing, which only makes detection slightly less strict.
/// </summary>
public static class WindowsSpellChecker
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> Cache = new(StringComparer.Ordinal);
    private static ISpellChecker? _checker;
    private static bool _unavailable;

    public static IReadOnlySet<string> Recognized(IEnumerable<string> words)
    {
        var recognized = new HashSet<string>(StringComparer.Ordinal);
        lock (Gate)
        {
            if (Checker() is not { } checker) return recognized;
            foreach (var word in words.Take(128))
            {
                if (!Cache.TryGetValue(word, out var known))
                {
                    known = IsCorrect(checker, word);
                    if (Cache.Count > 5_000) Cache.Clear();
                    Cache[word] = known;
                }
                if (known) recognized.Add(word);
            }
        }
        return recognized;
    }

    private static ISpellChecker? Checker()
    {
        if (_checker is not null || _unavailable) return _checker;
        try
        {
            var factory = (ISpellCheckerFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"))!)!;
            factory.IsSupported("en-US", out var supported);
            if (supported != 0) factory.CreateSpellChecker("en-US", out _checker);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException) { }
        _unavailable = _checker is null;
        return _checker;
    }

    private static bool IsCorrect(ISpellChecker checker, string word)
    {
        try
        {
            checker.Check(word, out var errors);
            var hasError = errors.Next(out var error) == 0;
            if (error != IntPtr.Zero) Marshal.Release(error);
            Marshal.ReleaseComObject(errors);
            return !hasError;
        }
        catch (COMException) { return false; }
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        void GetSupportedLanguages(out IntPtr languages);
        void IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int supported);
        void CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker checker);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        void GetLanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string tag);
        void Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError errors);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out IntPtr error);
    }
}
