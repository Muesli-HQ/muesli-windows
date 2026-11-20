namespace Muesli.Windows.Cotypist;

/// <summary>
/// macOS MUESLI_INCLUDE_COTYPIST: Cotypist is experimental and dev-builds-only. Every setting, download
/// and model load is refused unless this build includes Cotypist and its input helper (the TSF text
/// service DLL) is installed.
/// </summary>
public static class CotypistAvailability
{
    public const string HelperFileName = "Muesli.Cotypist.TextService.dll";

#if MUESLI_INCLUDE_COTYPIST
    public const bool IsIncludedInBuild = true;
#else
    public const bool IsIncludedInBuild = false;
#endif

    /// <summary>
    /// Where scripts/cotypist-register.ps1 installs the helper. TSF loads it into every app the user
    /// types in, so it lives outside the MSIX (whose files those apps can't load) and only an
    /// administrator can replace it; Muesli trusts pipe clients that have this exact file loaded.
    /// </summary>
    public static string HelperPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Muesli", "Cotypist", HelperFileName);

    public static bool IsHelperBundled => IsIncludedInBuild && File.Exists(HelperPath);
}
