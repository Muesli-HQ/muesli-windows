using System.Runtime.InteropServices;
using System.Text;

namespace Muesli.Windows.Core.Profiles;

/// <summary>
/// A development slot is a second package identity (MuesliDevA, MuesliDevB, …) that
/// <c>scripts/run-windows-dev.ps1</c> registers beside the normal Muesli.Windows install, so a
/// feature branch can be tested while another build keeps running. A slot has its own profile
/// folder and its own single-instance scope; the shipping identity is never a slot.
/// </summary>
public static class MuesliDevSlot
{
    public const string PackageNamePrefix = "MuesliDev";

    /// <summary>The slot's package name, e.g. <c>MuesliDevA</c>, or null for every other build.</summary>
    public static string? Current { get; } = Resolve(CurrentPackageName());

    internal static string? Resolve(string? packageName)
    {
        if (packageName is null ||
            !packageName.StartsWith(PackageNamePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var slot = packageName.AsSpan(PackageNamePrefix.Length);
        if (slot.Length is 0 or > 8) return null;
        foreach (var character in slot)
        {
            if (!char.IsAsciiLetterOrDigit(character)) return null;
        }

        return packageName;
    }

    private static string? CurrentPackageName()
    {
        try
        {
            var length = 0;
            // ERROR_INSUFFICIENT_BUFFER: packaged. Anything else, including
            // APPMODEL_ERROR_NO_PACKAGE, means there is no package identity to read.
            if (GetCurrentPackageFullName(ref length, null) != 122 || length <= 0) return null;
            var buffer = new StringBuilder(length);
            if (GetCurrentPackageFullName(ref length, buffer) != 0) return null;
            var fullName = buffer.ToString();
            var separator = fullName.IndexOf('_');
            return separator > 0 ? fullName[..separator] : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}
