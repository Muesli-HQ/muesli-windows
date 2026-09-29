using NAudio.Wave;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Media import: what Muesli advertises must match what it can actually decode, and anything it
/// refuses must come with a concrete way forward.
/// </summary>
public sealed class Phase8MediaImportTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void OnlyQualifiedExtensionsAreAdvertised()
    {
        Assert.NotEmpty(MediaImportFormats.Supported);
        Assert.All(MediaImportFormats.Supported, format =>
        {
            Assert.StartsWith(".", format.Extension, StringComparison.Ordinal);
            Assert.Equal(format.Extension.ToLowerInvariant(), format.Extension);
            Assert.False(string.IsNullOrWhiteSpace(format.Codec));
        });
        Assert.Equal(
            MediaImportFormats.SupportedExtensions.Count,
            MediaImportFormats.SupportedExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void VorbisOggIsNotAdvertisedBecauseWindowsCannotDecodeIt()
    {
        // Empirically confirmed: NAudio/Media Foundation throws opening a Vorbis .ogg on Windows.
        Assert.False(MediaImportFormats.IsSupported("meeting.ogg"));
        Assert.DoesNotContain(".ogg", MediaImportFormats.SupportedExtensions);
        Assert.DoesNotContain("*.ogg", MediaImportFormats.DialogFilter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("recording.wav")]
    [InlineData("recording.MP3")]
    [InlineData("recording.m4a")]
    [InlineData("recording.aac")]
    [InlineData("recording.mp4")]
    [InlineData("recording.mov")]
    [InlineData("recording.mkv")]
    [InlineData("recording.webm")]
    public void QualifiedFormatsAreAcceptedRegardlessOfCase(string path) =>
        Assert.True(MediaImportFormats.IsSupported(path));

    [Theory]
    [InlineData("clip.ogg")]
    [InlineData("clip.flac")]
    [InlineData("clip.wma")]
    [InlineData("clip.avi")]
    [InlineData("notes.txt")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedFilesAreRejected(string? path) => Assert.False(MediaImportFormats.IsSupported(path));

    [Theory]
    [InlineData("clip.ogg", "Vorbis")]
    [InlineData("clip.opus", "webm")]
    [InlineData("clip.flac", "FLAC")]
    [InlineData("clip.avi", "audio track")]
    public void RejectionGuidanceNamesTheReasonAndAConcreteNextStep(string path, string expectedDetail)
    {
        var guidance = MediaImportFormats.ConversionGuidanceFor(path);
        Assert.Contains(expectedDetail, guidance, StringComparison.OrdinalIgnoreCase);
        // Every refusal must tell the user what to do and reassure them about their original.
        Assert.Contains("Convert it to WAV", guidance, StringComparison.Ordinal);
        Assert.Contains("never modified or deleted", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownExtensionStillGetsActionableGuidance()
    {
        var guidance = MediaImportFormats.ConversionGuidanceFor("clip.xyz");
        Assert.Contains(".xyz", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Convert it to WAV", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportedFilesProduceNoGuidance() =>
        Assert.Equal("", MediaImportFormats.ConversionGuidanceFor("meeting.wav"));

    [Fact]
    public void TheDialogFilterOffersExactlyTheQualifiedFormats()
    {
        var filter = MediaImportFormats.DialogFilter;
        foreach (var extension in MediaImportFormats.SupportedExtensions)
        {
            Assert.Contains($"*{extension}", filter, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Decodes real human-speech fixtures and checks the decoded duration matches the source WAV.
    /// Gated on MUESLI_MEDIA_FIXTURE_DIR, a directory holding speech.wav plus one transcode per
    /// advertised extension. A format that cannot survive this must not stay on the supported list.
    /// </summary>
    [QualificationFact("MUESLI_MEDIA_FIXTURE_DIR")]
    public void EveryAdvertisedFormatDecodesRealSpeechToTheSourceDuration()
    {
        var directory = Environment.GetEnvironmentVariable("MUESLI_MEDIA_FIXTURE_DIR")!;

        var reference = Path.Combine(directory, "speech.wav");
        Assert.True(File.Exists(reference), $"The media fixture directory does not contain {reference}.");
        double referenceSeconds;
        using (var readerForReference = new AudioFileReader(reference))
        {
            referenceSeconds = readerForReference.TotalTime.TotalSeconds;
        }
        output.WriteLine($"reference speech.wav = {referenceSeconds:F3}s");

        foreach (var format in MediaImportFormats.Supported)
        {
            var fixturePath = Path.Combine(directory, $"speech{format.Extension}");
            Assert.True(File.Exists(fixturePath), $"Missing fixture for advertised format {format.Extension}");

            using var reader = new AudioFileReader(fixturePath);
            var seconds = reader.TotalTime.TotalSeconds;
            var buffer = new float[16000];
            long samples = 0;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) samples += read;

            output.WriteLine($"  {format.Extension,-6} {seconds,7:F3}s samples={samples}");
            Assert.True(samples > 0, $"{format.Extension} decoded no samples");
            Assert.True(Math.Abs(seconds - referenceSeconds) <= 0.35,
                $"{format.Extension} decoded {seconds:F3}s against a {referenceSeconds:F3}s source");
        }
    }

    // ---- source-file ownership ---------------------------------------------------

    [Fact]
    public void AnImportedOriginalIsNeverTreatedAsOwnedAudio()
    {
        using var directory = new TestDirectory();
        var storage = new CaptureStorageService(directory.Path);

        // Wherever the user's own media lives, it is not Muesli's to delete.
        Assert.False(storage.IsOwnedMeetingAudioPath("meet_1", @"C:\Users\someone\Videos\standup.mp4"));
        Assert.False(storage.IsOwnedMeetingAudioPath("meet_1", directory.File("imported.wav")));
        Assert.False(storage.IsOwnedMeetingAudioPath("meet_1", @"C:\Users\someone\Music\interview.mp3"));
    }

    [Fact]
    public void DeletingAMeetingNeverDeletesTheImportedOriginal()
    {
        using var directory = new TestDirectory();
        var original = directory.File("user-original.wav");
        File.WriteAllBytes(original, new byte[2048]);
        var storage = new CaptureStorageService(directory.Path);

        var result = storage.DeleteOwnedMeetingAudio("meet_import", [original]);

        Assert.True(File.Exists(original), "An imported original must survive meeting deletion.");
        Assert.Equal(0, result.DeletedCount);
    }

    [Fact]
    public void OnlyMuesliOwnedCaptureFilenamesInsideTheMeetingDirectoryAreDeletable()
    {
        using var directory = new TestDirectory();
        var storage = new CaptureStorageService(directory.Path);
        var owned = Path.Combine(directory.Path, "recordings", "meet_1", "microphone.wav");

        Assert.True(storage.IsOwnedMeetingAudioPath("meet_1", owned));
        // Another meeting's directory, and an arbitrary filename inside our own, are both refused.
        Assert.False(storage.IsOwnedMeetingAudioPath("meet_2", owned));
        Assert.False(storage.IsOwnedMeetingAudioPath("meet_1",
            Path.Combine(directory.Path, "recordings", "meet_1", "user-notes.docx")));
    }

    [Fact]
    public void PathTraversalCannotEscapeTheMeetingDirectory()
    {
        using var directory = new TestDirectory();
        var storage = new CaptureStorageService(directory.Path);
        var escape = Path.Combine(directory.Path, "recordings", "meet_1", "..", "..", "microphone.wav");

        Assert.False(storage.IsOwnedMeetingAudioPath("meet_1", escape));
    }

    [QualificationFact("MUESLI_MEDIA_FIXTURE_DIR")]
    public void DecodingIsDeterministicAcrossRepeatedReads()
    {
        var directory = Environment.GetEnvironmentVariable("MUESLI_MEDIA_FIXTURE_DIR")!;
        var path = Path.Combine(directory, "speech.mp3");
        Assert.True(File.Exists(path), $"The media fixture directory does not contain {path}.");

        static long Decode(string file)
        {
            using var reader = new AudioFileReader(file);
            var buffer = new float[16000];
            long total = 0;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) total += read;
            return total;
        }

        var first = Decode(path);
        Assert.Equal(first, Decode(path));
        Assert.Equal(first, Decode(path));
    }
}
