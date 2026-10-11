using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Muesli.Windows.Services;

/// <summary>
/// Keeps Windows from starving local ASR while Muesli is not the foreground app.
/// Dictation runs while another app has focus, so Windows 11 classes Muesli as background work:
/// EcoQoS power throttling moves it onto efficiency cores at low clocks, and normal priority
/// loses to every busy foreground app. The dictation latency log showed the same 6 s Parakeet
/// chunk decoding in ~0.2 s or ~3.5 s depending on that state, never in between.
/// </summary>
public static class TranscriptionPerformance
{
    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    private static readonly object Gate = new();
    private static int _boosts;
    private static ProcessPriorityClass _restorePriority = ProcessPriorityClass.Normal;

    /// <summary>Opts the process out of EcoQoS until <see cref="RestorePowerThrottling"/>.</summary>
    public static bool DisablePowerThrottling() => SetPowerThrottling(control: ProcessPowerThrottlingExecutionSpeed, state: 0);

    /// <summary>Test hook: forces EcoQoS on to reproduce the slow mode on demand.</summary>
    public static bool EnablePowerThrottling() =>
        SetPowerThrottling(control: ProcessPowerThrottlingExecutionSpeed, state: ProcessPowerThrottlingExecutionSpeed);

    /// <summary>Hands the throttling decision back to Windows.</summary>
    public static bool RestorePowerThrottling() => SetPowerThrottling(control: 0, state: 0);

    /// <summary>
    /// For the lifetime of the returned scope: no EcoQoS and AboveNormal priority. Measured on a
    /// 23.6 s clip (i7-13620H, Parakeet v3 CPU, two competing decodes): Windows' choice 17.9 s,
    /// throttling off 4.6-5.5 s, throttling off + AboveNormal 1.6 s. Nested scopes share one boost.
    /// </summary>
    public static IDisposable Boost()
    {
        lock (Gate)
        {
            if (_boosts++ == 0)
            {
                DisablePowerThrottling();
                try
                {
                    using var process = Process.GetCurrentProcess();
                    _restorePriority = process.PriorityClass;
                    if (_restorePriority is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
                    {
                        process.PriorityClass = ProcessPriorityClass.AboveNormal;
                    }
                }
                catch
                {
                    // Priority is an optimisation; a refused change must never fail a dictation.
                }
            }
        }

        return new BoostScope();
    }

    private static void EndBoost()
    {
        lock (Gate)
        {
            if (--_boosts != 0) return;
            RestorePowerThrottling();
            try
            {
                using var process = Process.GetCurrentProcess();
                if (process.PriorityClass == ProcessPriorityClass.AboveNormal)
                {
                    process.PriorityClass = _restorePriority;
                }
            }
            catch
            {
                // See Boost.
            }
        }
    }

    private static bool SetPowerThrottling(uint control, uint state)
    {
        var information = new PowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask = control,
            StateMask = state
        };
        return SetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottling,
            ref information,
            (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    private sealed class BoostScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) EndBoost();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref PowerThrottlingState information,
        uint informationSize);
}
