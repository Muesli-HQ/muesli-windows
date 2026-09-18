<#
.SYNOPSIS
    Captures a PNG of every visible top-level window belonging to the running
    unpackaged WinUI shell.

.DESCRIPTION
    Used for WinUI visual qualification against docs/ui-reference. Captures what is
    actually on screen; it never synthesises a frame, so a missing window produces a
    reported gap rather than an invented image.
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'Muesli.Windows.WinUI',
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Prefix = 'winui'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MuesliWindowProbe
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public int Left, Top, Width, Height;
    }

    public static List<WindowInfo> Visible(uint pid)
    {
        var found = new List<WindowInfo>();
        EnumWindows((hwnd, lParam) =>
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner != pid || !IsWindowVisible(hwnd)) return true;

            RECT rect;
            // DWMWA_EXTENDED_FRAME_BOUNDS = 9 excludes the invisible resize border.
            if (DwmGetWindowAttribute(hwnd, 9, out rect, Marshal.SizeOf(typeof(RECT))) != 0)
            {
                if (!GetWindowRect(hwnd, out rect)) return true;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < 80 || height < 80) return true;

            var title = new StringBuilder(512);
            GetWindowTextW(hwnd, title, title.Capacity);
            found.Add(new WindowInfo
            {
                Handle = hwnd,
                Title = title.ToString(),
                Left = rect.Left,
                Top = rect.Top,
                Width = width,
                Height = height
            });
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process) { throw "No running process named $ProcessName." }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$windows = [MuesliWindowProbe]::Visible([uint32]$process.Id)
if ($windows.Count -eq 0) { throw "Process $ProcessName (pid $($process.Id)) has no visible top-level window." }

$index = 0
foreach ($window in $windows) {
    $index++
    [MuesliWindowProbe]::SetForegroundWindow($window.Handle) | Out-Null
    Start-Sleep -Milliseconds 700

    $bitmap = New-Object System.Drawing.Bitmap $window.Width, $window.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($window.Left, $window.Top, 0, 0, $bitmap.Size)
        $safeTitle = ($window.Title -replace '[^A-Za-z0-9]+', '-').Trim('-')
        if (-not $safeTitle) { $safeTitle = 'untitled' }
        $path = Join-Path $OutputDirectory ("{0}-{1:d2}-{2}.png" -f $Prefix, $index, $safeTitle.ToLowerInvariant())
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        [pscustomobject]@{
            Title = $window.Title
            Size  = "$($window.Width)x$($window.Height)"
            Path  = $path
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}
