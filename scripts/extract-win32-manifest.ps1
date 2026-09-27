param(
    [string]$ExePath = "",
    [string]$MsixPath = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "release-common.ps1")

if ([string]::IsNullOrWhiteSpace($ExePath) -and [string]::IsNullOrWhiteSpace($MsixPath)) {
    throw "Pass -ExePath (RT_MANIFEST from an executable) or -MsixPath (AppxManifest.xml from an MSIX)."
}
if (-not [string]::IsNullOrWhiteSpace($MsixPath)) {
    if (-not (Test-Path -LiteralPath $MsixPath)) {
        throw "MSIX not found: $MsixPath"
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($MsixPath)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq "AppxManifest.xml" } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "The MSIX has no AppxManifest.xml at its root: $MsixPath"
        }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $archive.Dispose()
    }
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = Join-Path $root "artifacts\muesli-win32-manifest.xml"
    }
    Write-Utf8NoBomFile -Path $OutputPath -Content ($manifest.Trim() + "`n")
    Write-Host "Wrote AppxManifest.xml from MSIX: $OutputPath"
    return
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath"
}

if (-not ("L04ManifestNative" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class L04ManifestNative {
    private const uint LoadLibraryAsDatafile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr hResData);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr hModule);
    public static string ExtractRtManifest(string executablePath) {
        var module = LoadLibraryEx(executablePath, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero) throw new InvalidOperationException("LoadLibraryEx failed for RT_MANIFEST extraction.");
        try {
            var resource = FindResource(module, new IntPtr(1), new IntPtr(24));
            if (resource == IntPtr.Zero) throw new InvalidOperationException("Built executable has no RT_MANIFEST resource.");
            var size = SizeofResource(module, resource);
            var data = LoadResource(module, resource);
            if (size == 0 || data == IntPtr.Zero) throw new InvalidOperationException("RT_MANIFEST could not be loaded.");
            var bytes = new byte[size];
            Marshal.Copy(LockResource(data), bytes, 0, (int)size);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { FreeLibrary(module); }
    }
}
"@
}

$manifest = [L04ManifestNative]::ExtractRtManifest((Resolve-Path -LiteralPath $ExePath).Path)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $root "artifacts\muesli-win32-manifest.xml"
}
Write-Utf8NoBomFile -Path $OutputPath -Content ($manifest.Trim() + "`n")
Write-Host "Wrote Win32 RT_MANIFEST: $OutputPath"
