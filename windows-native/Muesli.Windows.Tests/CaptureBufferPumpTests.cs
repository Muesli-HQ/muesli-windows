using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

/// <summary>MAC-35 capture-pipeline matrix: stop, discard, stale callbacks, overflow and faults, with
/// exact PCM equality through a real WAV file.</summary>
public sealed class CaptureBufferPumpTests
{
    private static byte[] Packet(int index, int length = 960)
    {
        var packet = new byte[length];
        for (var i = 0; i < length; i++) packet[i] = (byte)(index * 31 + i);
        return packet;
    }

    [Fact]
    public async Task StopDrainsEveryAcceptedBufferInOrderIntoTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pump-{Guid.NewGuid():N}.wav");
        try
        {
            var format = new WaveFormat(16_000, 16, 1);
            var writer = new WaveFileWriter(path, format);
            // A slow writer: the queue is still full when stop arrives.
            var pump = new CaptureBufferPump((buffer, count) => { Thread.Sleep(1); writer.Write(buffer, 0, count); }, error => throw error);
            var expected = new List<byte>();
            var scratch = new byte[960];
            // One reused capture buffer, as WASAPI hands out: the pump must copy it.
            await Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    Packet(i).CopyTo(scratch, 0);
                    Assert.True(pump.Offer(scratch, scratch.Length));
                    expected.AddRange(Packet(i));
                    Array.Clear(scratch);
                }
            });
            await pump.DrainAsync();
            writer.Dispose();

            using var reader = new WaveFileReader(path);
            var actual = new byte[reader.Length];
            Assert.Equal(actual.Length, reader.Read(actual, 0, actual.Length));
            Assert.Equal(expected.ToArray(), actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CallbacksAfterStopAreRefused()
    {
        var consumed = 0;
        var pump = new CaptureBufferPump((_, _) => consumed++, error => throw error);
        Assert.True(pump.Offer(Packet(0), 960));
        await pump.DrainAsync();
        Assert.False(pump.Offer(Packet(1), 960));
        Assert.Equal(1, consumed);
    }

    [Fact]
    public async Task DiscardClosesDeliveryAndDropsWhatIsQueued()
    {
        using var first = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var consumed = new List<byte>();
        var pump = new CaptureBufferPump((buffer, count) =>
        {
            consumed.Add(buffer[0]);
            first.Set();
            release.Wait();
        }, error => throw error);
        for (var i = 0; i < 5; i++) Assert.True(pump.Offer(Packet(i), 960));
        first.Wait();
        pump.Discard();
        Assert.False(pump.Offer(Packet(9), 960));
        release.Set();
        await pump.DrainAsync();
        Assert.Equal([Packet(0)[0]], consumed);
    }

    [Fact]
    public async Task ADiscardAfterStopBeganCannotDropQueuedAudio()
    {
        using var release = new ManualResetEventSlim();
        var consumed = 0;
        var pump = new CaptureBufferPump((_, _) => { release.Wait(); consumed++; }, error => throw error);
        for (var i = 0; i < 5; i++) Assert.True(pump.Offer(Packet(i), 960));
        var drained = pump.DrainAsync();
        pump.Discard(); // an early UI shutdown after the stop request
        release.Set();
        await drained;
        Assert.Equal(5, consumed);
    }

    [Fact]
    public async Task AFullQueueRefusesNewBuffersButKeepsAcceptedOnesExact()
    {
        using var release = new ManualResetEventSlim();
        var consumed = new List<byte[]>();
        var pump = new CaptureBufferPump((buffer, count) => { release.Wait(); consumed.Add(buffer[..count]); }, error => throw error, slots: 4, slotBytes: 960);
        var accepted = Enumerable.Range(0, 10).Where(i => pump.Offer(Packet(i), 960)).ToList();
        Assert.Equal(4, accepted.Count);
        Assert.Equal(6, pump.Dropped);
        release.Set();
        await pump.DrainAsync();
        Assert.Equal(accepted.Select(i => Packet(i)), consumed);
        // Slots came back: a new pump-sized burst after draining is impossible (closed), but the pool is whole.
        Assert.False(pump.Offer(Packet(11), 960));
    }

    [Fact]
    public async Task OversizedPacketsAreCopiedWhole()
    {
        var consumed = new List<byte[]>();
        var pump = new CaptureBufferPump((buffer, count) => consumed.Add(buffer[..count]), error => throw error, slots: 2, slotBytes: 16);
        Assert.True(pump.Offer(Packet(1, 4_000), 4_000));
        Assert.True(pump.Offer(Packet(2, 8), 8));
        await pump.DrainAsync();
        Assert.Equal([Packet(1, 4_000), Packet(2, 8)], consumed);
    }

    [Fact]
    public async Task AWriterFailureIsReportedOnceAndStopsConsumption()
    {
        var faults = 0;
        var consumed = 0;
        var pump = new CaptureBufferPump((_, _) => { if (++consumed == 2) throw new IOException("disk full"); }, _ => faults++);
        for (var i = 0; i < 5; i++) pump.Offer(Packet(i), 960);
        await pump.DrainAsync();
        Assert.Equal(1, faults);
        Assert.Equal(2, consumed);
    }

    [Fact]
    public void EmptyPacketsAreNotAccepted()
    {
        var pump = new CaptureBufferPump((_, _) => { }, error => throw error);
        Assert.False(pump.Offer([], 0));
        pump.Discard();
    }
}
