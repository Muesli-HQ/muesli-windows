using Muesli.Windows.WinUI.Services;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

// Synthetic audio only: never captures the screen or microphone.
var folder = Directory.CreateTempSubdirectory("muesli-video-probe");
try
{
    var path = Path.Combine(folder.FullName, "probe.mp4");
    var source = new MediaStreamSource(new AudioStreamDescriptor(ScreenVideoRecorder.AudioInput())) { BufferTime = TimeSpan.Zero };
    var chunk = 0;
    source.SampleRequested += (_, args) =>
        args.Request.Sample = chunk < 20 ? ScreenVideoRecorder.AudioSample(Enumerable.Repeat((short)100, 1_600).ToArray(), chunk++ * 1_600L) : null;
    var file = await (await StorageFolder.GetFolderFromPathAsync(folder.FullName)).CreateFileAsync("probe.mp4");
    using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);
        profile.Video = null;
        profile.Audio = ScreenVideoRecorder.AudioOutput();
        var prepared = await new MediaTranscoder().PrepareMediaStreamSourceTranscodeAsync(source, output, profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException($"Cannot transcode: {prepared.FailureReason}");
        await prepared.TranscodeAsync();
    }
    var properties = await (await StorageFile.GetFileFromPathAsync(path)).Properties.GetMusicPropertiesAsync();
    if (properties.Duration.TotalSeconds < 1.9) throw new InvalidOperationException($"Duration {properties.Duration} is shorter than 2 s.");
    Console.WriteLine("PASS: production meeting AAC settings encode a two-second audio track");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL: {error.Message}");
    return 1;
}
finally
{
    try { folder.Delete(recursive: true); } catch (IOException) { }
}
