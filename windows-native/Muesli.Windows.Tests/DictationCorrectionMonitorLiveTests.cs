using System.Diagnostics;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// End-to-end check of the UI Automation monitor against a real window in another process: a
/// throwaway WinForms text box that "receives" a dictation and is then edited. Needs an interactive
/// desktop, so it runs only with MUESLI_LIVE_UIA=1.
/// </summary>
public sealed class DictationCorrectionMonitorLiveTests
{
    [LiveUiaFact]
    public async Task ReportsACorrectionMadeInAnotherApp()
    {
        const string original = "please send the notes to musley before lunch today";
        const string edited = "please send the notes to muesli before lunch today";
        var script = $$"""
            Add-Type -AssemblyName System.Windows.Forms
            $form = New-Object Windows.Forms.Form
            $form.Text = 'Muesli monitor test'
            $box = New-Object Windows.Forms.TextBox
            $box.Multiline = $true; $box.Dock = 'Fill'; $box.Text = '{{original}}'
            $form.Controls.Add($box)
            $timer = New-Object Windows.Forms.Timer
            $timer.Interval = 1200
            $timer.Add_Tick({ $box.Text = '{{edited}}'; $timer.Stop() })
            $form.Add_Shown({ $box.Focus(); $timer.Start() })
            [Windows.Forms.Application]::Run($form)
            """;
        using var host = Process.Start(new ProcessStartInfo("powershell.exe",
            ["-NoProfile", "-STA", "-Command", script]) { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var window = IntPtr.Zero;
            for (var attempt = 0; attempt < 100 && window == IntPtr.Zero; attempt++)
            {
                await Task.Delay(100);
                host.Refresh();
                window = host.MainWindowHandle;
            }
            Assert.NotEqual(IntPtr.Zero, window);

            var found = new TaskCompletionSource<DictionarySuggestion>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var monitor = new DictationCorrectionMonitor();
            monitor.Start(original, "Test|powershell", window, suggestion => found.TrySetResult(suggestion));
            var suggestion = await found.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(("musley", "muesli", "Test|powershell"), (suggestion.Observed, suggestion.Replacement, suggestion.AppContext));
        }
        finally
        {
            if (!host.HasExited) host.Kill();
        }
    }

    private sealed class LiveUiaFactAttribute : FactAttribute
    {
        public LiveUiaFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MUESLI_LIVE_UIA") != "1") Skip = "Set MUESLI_LIVE_UIA=1 on an interactive desktop.";
        }
    }
}
