using System.Runtime.InteropServices;

namespace Muesli.Windows.Services;

/// <summary>
/// Tells which words English spelling knows, through the Windows Spell Checking API
/// (<c>ISpellCheckerFactory</c>). The correction detector uses it as macOS uses
/// <c>NSSpellChecker</c>: so "usually" to "usual" is not offered as a dictionary entry. Results are
/// cached (5,000 words, as on macOS). When the API or the English dictionary is unavailable every
/// word counts as unknown, which is the detector's documented fallback.
/// </summary>
public sealed class WindowsSpellChecker
{
    private const int MaxCacheEntries = 5_000;
    private static readonly Guid SpellCheckerFactoryClsid = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private const int SFalse = 1;

    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private ISpellChecker? _checker;
    private bool _unavailable;

    /// <summary>The lower-cased words in <paramref name="candidates"/> that are spelled correctly in English.</summary>
    public IReadOnlySet<string> RecognizedWords(IEnumerable<string> candidates)
    {
        var recognized = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var candidate in candidates.Select(word => word.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal))
            {
                if (!_cache.TryGetValue(candidate, out var known))
                {
                    known = Check(candidate);
                    Store(candidate, known);
                }
                if (known) recognized.Add(candidate);
            }
        }
        return recognized;
    }

    private bool Check(string word)
    {
        var checker = Checker();
        if (checker is null) return false;
        try
        {
            if (checker.Check(word, out var errors) != 0 || errors is null) return false;
            try
            {
                var result = errors.Next(out var error);
                if (error != IntPtr.Zero) Marshal.Release(error);
                return result == SFalse;
            }
            finally
            {
                Marshal.ReleaseComObject(errors);
            }
        }
        catch (COMException)
        {
            return false;
        }
    }

    private ISpellChecker? Checker()
    {
        if (_checker is not null || _unavailable) return _checker;
        try
        {
            var type = Type.GetTypeFromCLSID(SpellCheckerFactoryClsid, throwOnError: false);
            if (type is not null && Activator.CreateInstance(type) is ISpellCheckerFactory factory)
            {
                foreach (var language in new[] { "en-US", "en-GB", "en" })
                {
                    if (factory.IsSupported(language, out var supported) == 0 && supported &&
                        factory.CreateSpellChecker(language, out var checker) == 0 && checker is not null)
                    {
                        _checker = checker;
                        break;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidComObjectException)
        {
        }

        _unavailable = _checker is null;
        return _checker;
    }

    private void Store(string word, bool known)
    {
        if (_cache.TryAdd(word, known)) _cacheOrder.Enqueue(word);
        while (_cacheOrder.Count > MaxCacheEntries) _cache.Remove(_cacheOrder.Dequeue());
    }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        [PreserveSig] int GetSupportedLanguages(out IntPtr value);
        [PreserveSig] int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, [MarshalAs(UnmanagedType.Bool)] out bool value);
        [PreserveSig] int CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out ISpellChecker? value);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [PreserveSig] int GetLanguageTag(out IntPtr value);
        [PreserveSig] int Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError? value);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out IntPtr value);
    }
}
