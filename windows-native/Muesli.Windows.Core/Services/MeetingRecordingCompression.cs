using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS recording_format: saved meeting tracks can be kept as AAC in M4A instead of PCM WAV.
/// Runs after the transcript is final, so transcription always reads the lossless capture.
/// </summary>
public static class MeetingRecordingCompression
{
    /// <summary>
    /// Encodes a saved WAV track to M4A beside it and deletes the WAV. Returns the path the meeting
    /// should point at: the M4A, or the untouched WAV when encoding is impossible.
    /// </summary>
    public static string? ToM4a(string? wavPath, Action<string, Exception>? logError = null)
    {
        if (wavPath is null || !wavPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(wavPath))
            return wavPath;
        var target = Path.ChangeExtension(wavPath, ".m4a");
        // Media Foundation picks the container from the extension, so the temporary file keeps it.
        var temporary = Path.ChangeExtension(wavPath, $".{Guid.NewGuid():N}.m4a");
        try
        {
            using (var reader = new AudioFileReader(wavPath))
            {
                // The Windows AAC encoder accepts only 44.1/48 kHz 16-bit PCM; captures are 16 kHz.
                var resampled = reader.WaveFormat.SampleRate is 44100 or 48000
                    ? (ISampleProvider)reader
                    : new WdlResamplingSampleProvider(reader, 48000);
                MediaFoundationEncoder.EncodeToAac(resampled.ToWaveProvider16(), temporary, 96000);
            }
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception exception)
        {
            try { File.Delete(temporary); } catch (IOException) { }
            logError?.Invoke($"Meeting audio was kept as WAV: {Path.GetFileName(wavPath)} could not be encoded.", exception);
            return wavPath;
        }

        try { File.Delete(wavPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logError?.Invoke($"{Path.GetFileName(wavPath)} was encoded but could not be removed.", exception);
        }
        return target;
    }
}
