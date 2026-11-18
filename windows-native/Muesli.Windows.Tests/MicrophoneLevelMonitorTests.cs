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

    [Fact]
    public void Extensible_formats_are_read_by_sub_format_not_bit_depth()
    {
        // Half of full scale as 32-bit integer PCM. Read as float it would be a tiny denormal.
        var ints = new[] { 1 << 30, -(1 << 29) };
        var intBytes = ints.SelectMany(BitConverter.GetBytes).ToArray();
        // NAudio's constructor assumes 32 bits means float; a device can report 32-bit integer PCM,
        // so set KSDATAFORMAT_SUBTYPE_PCM the way such a mix format arrives from Windows.
        var pcm32 = new WaveFormatExtensible(48000, 32, 2);
        typeof(WaveFormatExtensible)
            .GetField("subFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(pcm32, new Guid("00000001-0000-0010-8000-00aa00389b71"));
        Assert.Equal(0.5, MicrophoneLevelMonitor.Peak(intBytes, intBytes.Length, pcm32), 3);

        var floatBytes = new[] { 0.25f, -0.75f }.SelectMany(BitConverter.GetBytes).ToArray();
        var float32 = WaveFormatExtensible.CreateIeeeFloatWaveFormat(48000, 2);
        Assert.Equal(0.75, MicrophoneLevelMonitor.Peak(floatBytes, floatBytes.Length, float32), 3);

        // 24-bit PCM: -0.5 full scale is 0xC00000 little-endian.
        var pcm24 = new byte[] { 0x00, 0x00, 0xC0, 0x00, 0x10, 0x00 };
        Assert.Equal(0.5, MicrophoneLevelMonitor.Peak(pcm24, pcm24.Length, new WaveFormat(48000, 24, 1)), 3);
    }
}
