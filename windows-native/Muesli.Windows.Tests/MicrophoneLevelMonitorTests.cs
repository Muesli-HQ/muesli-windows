using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class MicrophoneLevelMonitorTests
{
    [Fact]
    public void Silence_is_empty_and_speech_fills_most_of_the_meter()
    {
        Assert.Equal(0, MicrophoneLevelMonitor.Normalize(0));
        Assert.Equal(0, MicrophoneLevelMonitor.Normalize(0.001));        // -60 dBFS room noise
        Assert.InRange(MicrophoneLevelMonitor.Normalize(0.1), 0.6, 0.7);  // -20 dBFS speech
        Assert.Equal(1, MicrophoneLevelMonitor.Normalize(1));
    }

    [Fact]
    public void Peak_reads_float_and_16_bit_buffers()
    {
        var floats = new[] { 0.1f, -0.5f, 0.25f };
        var floatBytes = floats.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal(0.5, MicrophoneLevelMonitor.Peak(floatBytes, floatBytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)), 3);

        var shorts = new short[] { 1000, -16384, 200 };
        var shortBytes = shorts.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal(0.5, MicrophoneLevelMonitor.Peak(shortBytes, shortBytes.Length, new WaveFormat(16000, 16, 1)), 3);
    }
}
