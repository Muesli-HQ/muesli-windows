using System.IO;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public sealed record MeetingPlaybackTrack(string Label, string Path)
{
    public override string ToString() => Label;
}

public sealed class MeetingRecordingPlaybackService : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public static IReadOnlyList<MeetingPlaybackTrack> SelectTracks(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return [];
        return sourcePath.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select((path, index) => new MeetingPlaybackTrack(index == 0 ? "Microphone / recording" : "Meeting audio", path))
            .Where(track => File.Exists(track.Path))
            .DistinctBy(track => Path.GetFullPath(track.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Load(MeetingPlaybackTrack track)
    {
        Close();
        _reader = new AudioFileReader(Path.GetFullPath(track.Path));
        _output = new WaveOutEvent();
        _output.Init(_reader);
    }

    public void Play()
    {
        if (_output is null || _reader is null) throw new InvalidOperationException("Select a recording track first.");
        if (_reader.CurrentTime >= _reader.TotalTime) _reader.CurrentTime = TimeSpan.Zero;
        _output.Play();
    }

    public void Pause() => _output?.Pause();

    public void Stop()
    {
        _output?.Stop();
        if (_reader is not null) _reader.CurrentTime = TimeSpan.Zero;
    }

    public void Seek(TimeSpan position)
    {
        if (_reader is null) return;
        _reader.CurrentTime = position < TimeSpan.Zero ? TimeSpan.Zero : position > _reader.TotalTime ? _reader.TotalTime : position;
    }

    public void Close()
    {
        if (_output is not null)
        {
            try { _output.Stop(); } catch { }
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Close();
}
