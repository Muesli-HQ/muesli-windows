using NAudio.Wave;
using System.IO;

namespace Muesli.Windows.Services;

internal static class MeetingGapRecoveryService
{
    /// <summary>Live sample indices are produced by <see cref="StreamingPcmNormalizer"/> at this rate.</summary>
    internal const int LiveSampleRate = 16000;

    public static Task<(TranscriptionResult Microphone, TranscriptionResult System)> BuildUnifiedOwnerResultsAsync(
        MeetingLiveTranscriptionResult live,
        MeetingAudioPaths tracks,
        NativeTranscriptionClient recoveryClient,
        CancellationToken cancellationToken) =>
        BuildUnifiedOwnerResultsAsync(
            live,
            tracks,
            recoveryClient.ModelId,
            (title, path) => recoveryClient.TranscribeFileAsync(title, path),
            cancellationToken);

    internal static async Task<(TranscriptionResult Microphone, TranscriptionResult System)> BuildUnifiedOwnerResultsAsync(
        MeetingLiveTranscriptionResult live,
        MeetingAudioPaths tracks,
        string recoveryModelId,
        Func<string, string, Task<TranscriptionResult>> transcribe,
        CancellationToken cancellationToken)
    {
        var mic = await BuildChannelAsync(LiveTranscriptChannel.Microphone, tracks.MicrophonePath, live, recoveryModelId, transcribe, cancellationToken).ConfigureAwait(false);
        var system = await BuildChannelAsync(LiveTranscriptChannel.System, tracks.SystemPath, live, recoveryModelId, transcribe, cancellationToken).ConfigureAwait(false);
        return (mic, system);
    }

    private static async Task<TranscriptionResult> BuildChannelAsync(
        LiveTranscriptChannel channel,
        string? audioPath,
        MeetingLiveTranscriptionResult live,
        string recoveryModelId,
        Func<string, string, Task<TranscriptionResult>> transcribe,
        CancellationToken cancellationToken)
    {
        var segments = live.Committed
            .Where(segment => segment.Channel == channel)
            .Select(segment => new TranscriptSegment(
                segment.Id,
                channel == LiveTranscriptChannel.Microphone ? "You" : "System audio",
                SamplesToMs(segment.StartSample),
                SamplesToMs(segment.EndSample),
                segment.Text))
            .ToList();
        if (audioPath is not null)
        {
            foreach (var gap in live.Gaps.Where(gap => gap.Channel == channel))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var clip = CreateClip(audioPath, gap);
                if (clip.Path is null) continue;
                var recovered = await transcribe("Meeting gap recovery", clip.Path).ConfigureAwait(false);
                foreach (var segment in recovered.Segments ?? [])
                {
                    var shifted = segment with
                    {
                        Id = $"gap_{channel}_{segments.Count + 1}_{segment.Id}",
                        Speaker = channel == LiveTranscriptChannel.Microphone ? "You" : "System audio",
                        StartMs = segment.StartMs + clip.StartMs,
                        EndMs = segment.EndMs + clip.StartMs
                    };
                    if (!segments.Any(existing => Overlaps(existing, shifted) && MeetingLiveTranscriptionSession.SameText(existing.Text, shifted.Text)))
                        segments.Add(shifted);
                }
            }
        }
        segments = segments.OrderBy(segment => segment.StartMs).ThenBy(segment => segment.EndMs).ToList();
        return new TranscriptionResult(
            string.Join(" ", segments.Select(segment => segment.Text)),
            $"Owner: {live.ModelId}; recovery model: {recoveryModelId}; measured gaps: {live.Gaps.Count(gap => gap.Channel == channel)}",
            segments.Count == 0 ? 0 : segments.Max(segment => segment.EndMs),
            segments);
    }

    private static GapClip CreateClip(string sourcePath, LiveTranscriptGap gap)
    {
        using var reader = new WaveFileReader(sourcePath);
        var sampleRate = reader.WaveFormat.SampleRate;
        var bytesPerFrame = reader.WaveFormat.BlockAlign;
        var totalFrames = reader.Length / bytesPerFrame;
        var padding = sampleRate / 4L;
        // Gap ranges are 16 kHz live-stream indices; the retained track keeps the device's
        // native rate, so the range has to be rescaled before it can address WAV frames.
        var startFrame = Math.Max(0, ToSourceFrame(gap.StartSample, sampleRate, totalFrames) - padding);
        var requestedEnd = gap.EndSample == long.MaxValue
            ? totalFrames
            : ToSourceFrame(gap.EndSample, sampleRate, totalFrames) + padding;
        var endFrame = Math.Min(totalFrames, Math.Max(startFrame, requestedEnd));
        if (endFrame <= startFrame) return new GapClip(null, 0);

        var destination = Path.Combine(Path.GetDirectoryName(sourcePath)!, $".muesli-gap-{Guid.NewGuid():N}.wav");
        reader.Position = startFrame * bytesPerFrame;
        using (var writer = new WaveFileWriter(destination, reader.WaveFormat))
        {
            var remaining = (endFrame - startFrame) * bytesPerFrame;
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) break;
                writer.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        return new GapClip(destination, (int)Math.Round(startFrame * 1000.0 / sampleRate));
    }

    private static bool Overlaps(TranscriptSegment first, TranscriptSegment second) =>
        Math.Min(first.EndMs, second.EndMs) >= Math.Max(first.StartMs, second.StartMs);
    private static int SamplesToMs(long samples) => (int)Math.Clamp(Math.Round(samples * 1000.0 / LiveSampleRate), 0, int.MaxValue);

    internal static long ToSourceFrame(long liveSample, int sourceSampleRate, long totalFrames) =>
        (long)Math.Clamp(Math.Round(Math.Max(0, liveSample) * (sourceSampleRate / (double)LiveSampleRate)), 0, totalFrames);

    private sealed class GapClip(string? path, int startMs) : IDisposable
    {
        public string? Path { get; } = path;
        public int StartMs { get; } = startMs;
        public void Dispose()
        {
            try { if (Path is not null && File.Exists(Path)) File.Delete(Path); } catch { }
        }
    }
}
