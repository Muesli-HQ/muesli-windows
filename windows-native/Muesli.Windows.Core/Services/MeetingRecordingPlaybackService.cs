using System.IO;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public enum MeetingPlaybackState
{
    Empty,
    Stopped,
    Playing,
    Paused,
    Failed
}

public sealed record MeetingPlaybackTrack(string Label, string Path)
{
    public override string ToString() => Label;
}

public sealed class MeetingPlaybackStateChangedEventArgs(
    MeetingPlaybackState state,
    TimeSpan position,
    TimeSpan duration,
    string? failure = null) : EventArgs
{
    public MeetingPlaybackState State { get; } = state;
    public TimeSpan Position { get; } = position;
    public TimeSpan Duration { get; } = duration;
    public string? Failure { get; } = failure;
}

public sealed class MeetingRecordingPlaybackService : IDisposable
{
    public const int WaveformBucketCount = WaveformPeakCache.BucketCount;

    private readonly object _gate = new();
    private readonly IWaveformPeakCache _waveformCache;
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private IReadOnlyList<double> _waveformPeaks = [];
    private MeetingPlaybackState _state = MeetingPlaybackState.Empty;
    private int _disposed;

    public MeetingRecordingPlaybackService(IWaveformPeakCache? waveformCache = null)
    {
        _waveformCache = waveformCache ?? new WaveformPeakCache();
    }

    public IReadOnlyList<double> WaveformPeaks
    {
        get { lock (_gate) return _waveformPeaks; }
    }

    public event EventHandler<MeetingPlaybackStateChangedEventArgs>? StateChanged;

    public MeetingPlaybackState State
    {
        get { lock (_gate) return _state; }
    }

    public TimeSpan Position
    {
        get { lock (_gate) return _reader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan Duration
    {
        get { lock (_gate) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    public static IReadOnlyList<MeetingPlaybackTrack> SelectTracks(
        string? microphonePath,
        string? systemPath,
        string? legacySourcePath = null)
    {
        var tracks = new List<MeetingPlaybackTrack>();
        AddIfPlayable(tracks, "Microphone (You)", microphonePath);
        AddIfPlayable(tracks, "Meeting audio (Others)", systemPath);
        if (!string.IsNullOrWhiteSpace(legacySourcePath))
        {
            var sources = legacySourcePath.Split(
                ';',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < sources.Length; index++)
            {
                if (tracks.Any(track => string.Equals(track.Path, sources[index], StringComparison.OrdinalIgnoreCase))) continue;
                AddIfPlayable(tracks, index == 0 ? "Recording" : $"Recording {index + 1}", sources[index]);
            }
        }
        return tracks;
    }

    public void Load(MeetingPlaybackTrack track)
        => LoadAsync(track).GetAwaiter().GetResult();

    public async Task LoadAsync(
        MeetingPlaybackTrack track,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var path = Path.GetFullPath(track.Path);
        if (!File.Exists(path))
        {
            var missing = new FileNotFoundException("Meeting recording was not found.", path);
            MarkLoadFailed();
            throw missing;
        }

        IReadOnlyList<double> peaks;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
            }

            var cached = await _waveformCache.TryGetAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                peaks = cached;
            }
            else
            {
                peaks = await WaveformPeakSampler.SampleAsync(path, cancellationToken).ConfigureAwait(false);
                try
                {
                    await _waveformCache.SaveAsync(path, peaks, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException) { /* Cache failure must not block playback. */ }
                catch (UnauthorizedAccessException) { /* Cache failure must not block playback. */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            MarkLoadFailed();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        AudioFileReader? preparedReader = null;
        WaveOutEvent? preparedOutput = null;
        try
        {
            preparedReader = await Task.Run(() => new AudioFileReader(path), cancellationToken).ConfigureAwait(false);
            preparedReader.Position = 0;
            preparedOutput = new WaveOutEvent();
            preparedOutput.PlaybackStopped += OnPlaybackStopped;
            preparedOutput.Init(preparedReader);

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                DisposePlaybackUnderGate();
                _reader = preparedReader;
                _output = preparedOutput;
                _waveformPeaks = peaks;
                preparedReader = null;
                preparedOutput = null;
                SetStateUnderGate(MeetingPlaybackState.Stopped);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    DisposePlaybackUnderGate();
                    _waveformPeaks = [];
                    SetStateUnderGate(MeetingPlaybackState.Failed);
                }
            }
            throw;
        }
        finally
        {
            if (preparedOutput is not null)
            {
                preparedOutput.PlaybackStopped -= OnPlaybackStopped;
                try { preparedOutput.Stop(); } catch { }
                preparedOutput.Dispose();
            }
            preparedReader?.Dispose();
        }
    }

    public Task RemoveWaveformCacheAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        _waveformCache.RemoveForSourceAsync(sourcePath, cancellationToken);

    public Task<int> CleanupWaveformCacheAsync(
        CancellationToken cancellationToken = default) =>
        _waveformCache.CleanupAsync(cancellationToken);

    private void MarkLoadFailed()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            DisposePlaybackUnderGate();
            _waveformPeaks = [];
            SetStateUnderGate(MeetingPlaybackState.Failed);
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_output is null || _reader is null)
            {
                throw new InvalidOperationException("Load a meeting recording before playback.");
            }
            if (_reader.CurrentTime >= _reader.TotalTime)
            {
                _reader.CurrentTime = TimeSpan.Zero;
            }
            _output.Play();
            SetStateUnderGate(MeetingPlaybackState.Playing);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_output is null || _state != MeetingPlaybackState.Playing)
            {
                return;
            }
            _output.Pause();
            SetStateUnderGate(MeetingPlaybackState.Paused);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_output is null)
            {
                return;
            }
            _output.Stop();
            if (_reader is not null)
            {
                _reader.CurrentTime = TimeSpan.Zero;
            }
            SetStateUnderGate(MeetingPlaybackState.Stopped);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            DisposePlaybackUnderGate();
            _waveformPeaks = [];
            SetStateUnderGate(MeetingPlaybackState.Empty);
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_reader is null)
            {
                return;
            }
            _reader.CurrentTime = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > _reader.TotalTime
                    ? _reader.TotalTime
                    : position;
            RaiseStateChangedUnderGate();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        lock (_gate)
        {
            DisposePlaybackUnderGate();
            _waveformPeaks = [];
            _state = MeetingPlaybackState.Empty;
        }
    }

    private static void AddIfPlayable(List<MeetingPlaybackTrack> tracks, string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && !tracks.Any(track => track.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                tracks.Add(new MeetingPlaybackTrack(label, fullPath));
            }
        }
        catch
        {
            // A malformed legacy path is simply not playable.
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        lock (_gate)
        {
            if (args.Exception is not null)
            {
                _state = MeetingPlaybackState.Failed;
                StateChanged?.Invoke(this, new MeetingPlaybackStateChangedEventArgs(
                    _state,
                    _reader?.CurrentTime ?? TimeSpan.Zero,
                    _reader?.TotalTime ?? TimeSpan.Zero,
                    args.Exception.GetType().Name));
                return;
            }
            if (_state == MeetingPlaybackState.Playing)
            {
                _state = MeetingPlaybackState.Stopped;
                RaiseStateChangedUnderGate();
            }
        }
    }

    private void SetStateUnderGate(MeetingPlaybackState state)
    {
        _state = state;
        RaiseStateChangedUnderGate();
    }

    private void RaiseStateChangedUnderGate() => StateChanged?.Invoke(
        this,
        new MeetingPlaybackStateChangedEventArgs(
            _state,
            _reader?.CurrentTime ?? TimeSpan.Zero,
            _reader?.TotalTime ?? TimeSpan.Zero));

    private void DisposePlaybackUnderGate()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Stop(); } catch { }
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
