using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Muesli.Windows.Services;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Port of macOS ScreenVideoRecorder (2f10b23d): one uninterrupted clip of a window or display.
/// Windows.Graphics.Capture supplies frames (with the system capture border as the visible disclosure);
/// MediaTranscoder encodes HEVC, falling back to H.264, at most 2560 px wide, 15 fps, about 2 Mbps.
/// Audio is the meeting's existing 16 kHz mic/system feed, never another device: AAC 16 kHz mono 32 kbps.
/// </summary>
public sealed class ScreenVideoRecorder : IAsyncDisposable
{
    public const int SampleRate = 16_000;
    private const int Chunk = 1_600;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 15);
    // AEC can retain audio awaiting a reference; delay encoding, not presentation timestamps.
    private static readonly TimeSpan EncodeDelay = TimeSpan.FromSeconds(3.5);

    private readonly string _path;
    private readonly Func<long> _meetingSampleNow;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _frameArrived = new(0);
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private MediaStreamSource? _source;
    private IRandomAccessStream? _output;
    private Task? _transcode;
    private SharedFrame? _latest;
    private SharedFrame? _pending;
    private TimeSpan? _firstTime;
    private TimeSpan _lastVideoTime;
    private DateTime _startedAtUtc;
    private MeetingVideoAudioBuffer? _audio;
    private long _audioSamples;
    private long? _stopTarget;
    private DateTime _drainDeadline;
    private DateTime _lastSpaceCheck = DateTime.MinValue;
    private volatile bool _stopping;
    private string? _failure;

    public ScreenVideoRecorder(string path, Func<long> meetingSampleNow)
    {
        _path = path;
        _meetingSampleNow = meetingSampleNow;
    }

    public event Action<string>? Failed;

    public int PixelWidth { get; private set; }
    public int PixelHeight { get; private set; }

    public async Task StartAsync(GraphicsCaptureItem item)
    {
        _item = item;
        _device = Direct3D.CreateDevice();
        var size = new SizeInt32 { Width = Math.Max(2, item.Size.Width / 2 * 2), Height = Math.Max(2, item.Size.Height / 2 * 2) };
        var scale = Math.Min(1.0, 2560.0 / size.Width);
        PixelWidth = Math.Max(2, (int)(size.Width * scale) / 2 * 2);
        PixelHeight = Math.Max(2, (int)(size.Height * scale) / 2 * 2);

        var video = new VideoStreamDescriptor(VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)size.Width, (uint)size.Height));
        var sound = new AudioStreamDescriptor(AudioInput());
        _source = new MediaStreamSource(video, sound) { BufferTime = TimeSpan.Zero, CanSeek = false };
        _source.SampleRequested += OnSampleRequested;

        // The pool keeps its first size: a resized window is cropped or padded, never a mid-stream format change.
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = true;
        try { _session.MinUpdateInterval = FrameInterval; } catch (Exception) { /* Older Windows: frames are thinned below. */ }
        item.Closed += (_, _) => Fail("The shared window or display closed.");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var file = await (await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(_path)))
            .CreateFileAsync(Path.GetFileName(_path), CreationCollisionOption.ReplaceExisting);
        _output = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        PrepareTranscodeResult? prepared = null;
        foreach (var profile in new[] { Profile(MediaEncodingProfile.CreateHevc(VideoEncodingQuality.Auto)), Profile(MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto)) })
        {
            try
            {
                prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(_source, _output, profile);
                if (prepared.CanTranscode) break;
            }
            catch (Exception) { prepared = null; }
        }
        if (prepared is not { CanTranscode: true }) throw new InvalidOperationException("A video encoder is unavailable.");
        _session.StartCapture();
        _transcode = prepared.TranscodeAsync().AsTask();
    }

    private MediaEncodingProfile Profile(MediaEncodingProfile profile)
    {
        profile.Video.Width = (uint)PixelWidth;
        profile.Video.Height = (uint)PixelHeight;
        profile.Video.Bitrate = 2_000_000;
        profile.Video.FrameRate.Numerator = 15;
        profile.Video.FrameRate.Denominator = 1;
        profile.Audio = AudioOutput();
        return profile;
    }

    /// <summary>Production audio track settings: AAC 16 kHz mono at 32 kbps.</summary>
    public static AudioEncodingProperties AudioOutput() => AudioEncodingProperties.CreateAac(SampleRate, 1, 32_000);

    /// <summary>Production PCM input: 16-bit mono at 16 kHz.</summary>
    public static AudioEncodingProperties AudioInput() => AudioEncodingProperties.CreatePcm(SampleRate, 1, 16);

    /// <summary>One PCM sample buffer at its position on the clip's 16 kHz timeline.</summary>
    public static MediaStreamSample AudioSample(short[] pcm, long position)
    {
        var bytes = new byte[pcm.Length * 2];
        System.Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), TimeSpan.FromSeconds((double)position / SampleRate));
    }

    /// <summary>Meeting audio on the 16 kHz meeting clock; late samples keep their position.</summary>
    public void AppendAudio(ReadOnlySpan<float> samples, long startSample, bool microphone)
    {
        var pcm = new short[samples.Length];
        for (var index = 0; index < samples.Length; index++) pcm[index] = (short)Math.Clamp(samples[index] * 32767f, short.MinValue, short.MaxValue);
        lock (_gate) _audio?.Append(pcm, startSample, microphone);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        lock (_gate)
        {
            if (_stopping || _failure is not null || (_pending ?? _latest) is { } previous && frame.SystemRelativeTime - previous.Time < FrameInterval * 0.9)
            {
                frame.Dispose();
                return;
            }
            _pending?.Release();
            _pending = new SharedFrame(frame);
        }
        _frameArrived.Release();
    }

    private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            args.Request.Sample = args.Request.StreamDescriptor is VideoStreamDescriptor ? await NextVideoAsync() : await NextAudioAsync();
        }
        catch (Exception error)
        {
            Fail(error.Message);
            args.Request.Sample = null;
        }
        finally { deferral.Complete(); }
    }

    private async Task<MediaStreamSample?> NextVideoAsync()
    {
        while (true)
        {
            // A static screen produces no frames; repeat the last one so audio keeps flowing.
            var arrived = await _frameArrived.WaitAsync(_firstTime is null ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500));
            lock (_gate)
            {
                if (_stopping || _failure is not null) return null;
                if (arrived && _pending is { } next)
                {
                    _latest?.Release();
                    _latest = next;
                    _pending = null;
                }
                if (_latest is not { } frame) continue;
                if (_firstTime is null)
                {
                    _firstTime = frame.Time;
                    _startedAtUtc = DateTime.UtcNow;
                    _audio = new MeetingVideoAudioBuffer(_meetingSampleNow());
                }
                var time = arrived ? frame.Time - _firstTime.Value : _lastVideoTime + TimeSpan.FromMilliseconds(500);
                if (time <= _lastVideoTime && _lastVideoTime > TimeSpan.Zero) time = _lastVideoTime + FrameInterval;
                _lastVideoTime = time;
                var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, time);
                frame.Retain();
                sample.Processed += (_, _) => { lock (_gate) frame.Release(); };
                return sample;
            }
        }
    }

    private async Task<MediaStreamSample?> NextAudioAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_failure is not null) return null;
                if (_firstTime is not null)
                {
                    if (DateTime.UtcNow - _lastSpaceCheck > TimeSpan.FromSeconds(5))
                    {
                        _lastSpaceCheck = DateTime.UtcNow;
                        if (FreeBytes() <= 2_000_000_000L) { FailLocked("Screen recording stopped: disk space is low."); return null; }
                    }
                    var elapsed = DateTime.UtcNow - _startedAtUtc;
                    var target = _stopTarget ?? (long)(Math.Max(0, (elapsed - EncodeDelay).TotalSeconds) * SampleRate);
                    if (target - _audioSamples > 160_000) { FailLocked("Audio encoding fell behind."); return null; }
                    if (target - _audioSamples >= Chunk)
                    {
                        var sample = AudioSample(_audio?.Take(Chunk) ?? new short[Chunk], _audioSamples);
                        _audioSamples += Chunk;
                        return sample;
                    }
                    // Backpressure must not prevent finalization forever: after the bounded drain window,
                    // keep the encoded media even if the audio tail is incomplete.
                    if (_stopping) return null;
                }
                else if (_stopping) return null;
            }
            await Task.Delay(_stopping ? 10 : 50);
        }
    }

    /// <summary>Finalizes the clip. The timeout releases only the waiter; the transcode keeps going and
    /// <paramref name="finalized"/> runs on its real completion.</summary>
    public async Task<double> StopAsync(TimeSpan timeout, Action<bool>? finalized = null)
    {
        lock (_gate)
        {
            if (_firstTime is not null)
                _stopTarget = (long)((DateTime.UtcNow - _startedAtUtc).TotalSeconds * SampleRate);
            _drainDeadline = DateTime.UtcNow.AddSeconds(2);
        }
        _session?.Dispose();
        _session = null;
        // Let audio drain up to the stop target for at most two seconds before ending both streams.
        while (DateTime.UtcNow < _drainDeadline)
        {
            lock (_gate) if (_failure is not null || _stopTarget is null || _stopTarget - _audioSamples < Chunk) break;
            await Task.Delay(10);
        }
        _stopping = true;
        _frameArrived.Release();
        if (_transcode is not { } transcode) throw new InvalidOperationException(_failure ?? "Screen capture did not start.");
        var completion = transcode.ContinueWith(task =>
        {
            var ok = task.IsCompletedSuccessfully && _firstTime is not null;
            Cleanup();
            finalized?.Invoke(ok);
            return ok;
        }, TaskScheduler.Default);
        if (await Task.WhenAny(completion, Task.Delay(timeout)) != completion)
            throw new TimeoutException("Screen finalization is continuing in the background.");
        if (!await completion) throw new InvalidOperationException(_failure ?? "No screen frames were captured.");
        lock (_gate) return Math.Max((double)_audioSamples / SampleRate, _lastVideoTime.TotalSeconds);
    }

    private void Cleanup()
    {
        lock (_gate)
        {
            _pending?.Release(); _pending = null;
            _latest?.Release(); _latest = null;
            _audio = null;
        }
        _pool?.Dispose(); _pool = null;
        _output?.Dispose(); _output = null;
        _device?.Dispose(); _device = null;
    }

    private long FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(_path)!).AvailableFreeSpace; }
        catch (Exception) { return 0; }
    }

    private void Fail(string message)
    {
        lock (_gate) FailLocked(message);
    }

    private void FailLocked(string message)
    {
        if (_failure is not null) return;
        _failure = message;
        _audio = null;
        _ = Task.Run(() => Failed?.Invoke(message));
    }

    public async ValueTask DisposeAsync()
    {
        if (_transcode is not null && !_transcode.IsCompleted)
        {
            try { await StopAsync(TimeSpan.FromSeconds(8)); } catch (Exception) { }
        }
        else Cleanup();
    }

    /// <summary>A capture frame kept alive while the encoder still references its surface.</summary>
    private sealed class SharedFrame(Direct3D11CaptureFrame frame)
    {
        private int _references = 1;
        public TimeSpan Time { get; } = frame.SystemRelativeTime;
        public IDirect3DSurface Surface => frame.Surface;
        public void Retain() => _references++;
        public void Release()
        {
            if (--_references == 0) frame.Dispose();
        }
    }

    private static class Direct3D
    {
        private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static IDirect3DDevice CreateDevice()
        {
            const int Hardware = 1, Warp = 5;
            const uint BgraSupport = 0x20, SdkVersion = 7;
            var result = D3D11CreateDevice(IntPtr.Zero, Hardware, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out var device, out _, out var context);
            if (result < 0) Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, Warp, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out device, out _, out context));
            try
            {
                var iid = DxgiDevice;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                    finally { Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgi); }
            }
            finally
            {
                Marshal.Release(context);
                Marshal.Release(device);
            }
        }
    }
}
