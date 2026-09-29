using System.Security.Cryptography;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Restores the automatic Markdown export safety net (L41/L43/AUTO-01) that was retired with the
/// WPF test project. Exercises the shared atomic exporter's manifest integrity, collision
/// behaviour, crash recovery, and concurrency on the active tree.
/// </summary>
public sealed class PostMeetingAutoExportTests
{
    private static MeetingItem Meeting(
        string id = "phase9",
        string title = "Planning / launch & review",
        string transcript = "transcript exact value α",
        string summary = "generated exact value β",
        string manualNotes = "manual exact value γ",
        MeetingSessionState sessionState = MeetingSessionState.Completed,
        PostMeetingAutomationResult? automationResult = null) =>
        new(
            Id: id,
            Title: title,
            CreatedAt: new DateTime(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc),
            Transcript: transcript,
            Summary: summary,
            SourcePath: @"C:\private\audio.wav",
            ModelProfile: "parakeet-v3",
            DurationMs: 65_000,
            FolderId: "folder-1",
            WordCount: 123,
            TemplateName: "Action items",
            HealthWarnings: ["low microphone level"],
            SessionState: sessionState,
            MicrophoneAudioPath: @"C:\private\microphone.wav",
            SystemAudioPath: @"C:\private\system.wav",
            RecoveredFromInterruption: true,
            ManualNotes: manualNotes,
            AutomationResult: automationResult);

    private static PostMeetingAutomationOptions AutoExport(string directory, MeetingExportMode mode = MeetingExportMode.FullMeeting) => new()
    {
        AutoExportEnabled = true,
        AutoExportDirectory = directory,
        AutoExportMode = mode
    };

    [Fact]
    public async Task AutoExportCreatesMissingDirectoryAtomicallyAndReusesManifest()
    {
        using var root = new TestDirectory();
        var destination = Path.Combine(root.Path, "selected destination");
        var options = AutoExport(destination);
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);
        var second = await service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.True(first.Export.Completed, first.Export.Error);
        Assert.True(second.Export.Completed, second.Export.Error);
        Assert.Equal(AutomationDestinationOwnership.UserSelectedDestination, first.Export.DestinationOwnership);
        Assert.Equal(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(
            MeetingExportFormatter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null),
            File.ReadAllText(first.Export.DestinationPath!));
        Assert.Single(Directory.GetFiles(destination, "*.md"));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
    }

    [Fact]
    public async Task ModifiedManifestDestinationIsNeverOverwrittenAndPublishesFreshCandidate()
    {
        using var directory = new TestDirectory();
        var meeting = Meeting();
        var options = AutoExport(directory.Path);
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(meeting, options, PostMeetingCompletionEvent.RecordingCompleted);
        Assert.True(first.Export.Completed, first.Export.Error);

        const string userContent = "user-modified export; preserve this file";
        File.WriteAllText(first.Export.DestinationPath!, userContent);

        var second = await service.RunAsync(meeting, options, PostMeetingCompletionEvent.RecordingCompleted);
        var expectedMarkdown = MeetingExportFormatter.BuildMarkdown(meeting, MeetingExportMode.FullMeeting, null);

        Assert.True(second.Export.Completed, second.Export.Error);
        Assert.NotEqual(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(userContent, File.ReadAllText(first.Export.DestinationPath!));
        Assert.Equal(expectedMarkdown, File.ReadAllText(second.Export.DestinationPath!));
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);

        var third = await service.RunAsync(meeting, options, PostMeetingCompletionEvent.RecordingCompleted);
        Assert.Equal(second.Export.DestinationPath, third.Export.DestinationPath);
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public async Task ChangedRenderedContentInvalidatesManifestAndPublishesCollisionFreeCandidate()
    {
        using var directory = new TestDirectory();
        var original = Meeting();
        var options = AutoExport(directory.Path);
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(original, options, PostMeetingCompletionEvent.RecordingCompleted);
        Assert.True(first.Export.Completed, first.Export.Error);
        var originalMarkdown = File.ReadAllText(first.Export.DestinationPath!);

        var changed = Meeting(transcript: "updated transcript exact value δ");
        var second = await service.RunAsync(changed, options, PostMeetingCompletionEvent.RecordingCompleted);
        var changedMarkdown = MeetingExportFormatter.BuildMarkdown(changed, MeetingExportMode.FullMeeting, null);

        Assert.True(second.Export.Completed, second.Export.Error);
        Assert.NotEqual(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(originalMarkdown, File.ReadAllText(first.Export.DestinationPath!));
        Assert.Equal(changedMarkdown, File.ReadAllText(second.Export.DestinationPath!));
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public async Task ConcurrentAutoExportsPublishExactlyOneMarkdownFile()
    {
        const int rounds = 8;
        const int concurrentRuns = 12;
        for (var round = 0; round < rounds; round++)
        {
            using var directory = new TestDirectory();
            var options = AutoExport(directory.Path) with
            {
                RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 3 }
            };
            var service = new PostMeetingAutomationService();

            var runs = await Task.WhenAll(Enumerable.Range(0, concurrentRuns).Select(_ => Task.Run(() =>
                service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted))));

            Assert.All(runs, result => Assert.True(result.Export.Completed, result.Export.Error));
            Assert.Single(runs.Select(result => result.Export.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.Single(Directory.GetFiles(directory.Path, "*.md"));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }
    }

    [Fact]
    public async Task PreexistingUnownedControlDirectoryIsLeftCompletelyUntouched()
    {
        using var directory = new TestDirectory();
        var controlDirectory = Path.Combine(directory.Path, ".muesli-automation");
        Directory.CreateDirectory(controlDirectory);
        var sentinel = Path.Combine(controlDirectory, "user-file.txt");
        File.WriteAllText(sentinel, "do not modify");
        var beforeAttributes = File.GetAttributes(controlDirectory);

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(), AutoExport(directory.Path), PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Failed, result.Status);
        Assert.False(result.Export.Completed);
        Assert.Equal("do not modify", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(controlDirectory, ".owner.json")));
        Assert.Equal(beforeAttributes, File.GetAttributes(controlDirectory));
        Assert.Single(Directory.GetFiles(controlDirectory));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.md"));
    }

    [Fact]
    public async Task PreexistingMatchingServiceTempCollisionIsNeverDeleted()
    {
        using var directory = new TestDirectory();
        var service = new PostMeetingAutomationService();
        var options = AutoExport(directory.Path) with
        {
            RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 1 }
        };
        var setup = await service.RunAsync(
            Meeting(id: "control-owner-setup"), options, PostMeetingCompletionEvent.RecordingCompleted);
        Assert.True(setup.Export.Completed, setup.Export.Error);

        var target = Meeting(id: "temp-collision-target");
        var paths = PostMeetingMarkdownAutoExporter.GetControlPaths(
            directory.Path,
            target.Id,
            PostMeetingCompletionEvent.RecordingCompleted,
            MeetingExportMode.FullMeeting);
        var collisionPath = Path.Combine(directory.Path, $".muesli-{paths.Key}.content-collision.tmp");
        File.WriteAllText(collisionPath, "preexisting user temp");
        var tokens = new Queue<string>(["claim-owned", "content-collision"]);
        PostMeetingMarkdownAutoExporter.TemporaryTokenFactoryForTests = () => tokens.Dequeue();
        try
        {
            var result = await service.RunAsync(target, options, PostMeetingCompletionEvent.RecordingCompleted);

            Assert.False(result.Export.Completed);
            Assert.Equal("preexisting user temp", File.ReadAllText(collisionPath));
        }
        finally
        {
            PostMeetingMarkdownAutoExporter.TemporaryTokenFactoryForTests = null;
        }
    }

    [Fact]
    public async Task UserCollisionIsNeverOverwrittenAndManifestReusesTheNewCandidate()
    {
        using var directory = new TestDirectory();
        var meeting = Meeting();
        var suggested = Path.ChangeExtension(
            MeetingExportFormatter.SuggestFilename(meeting, MeetingExportMode.FullMeeting), ".md");
        var userFile = directory.File(suggested);
        File.WriteAllText(userFile, "user-owned content");
        var options = AutoExport(directory.Path);
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(meeting, options, PostMeetingCompletionEvent.RecordingCompleted);
        var second = await service.RunAsync(meeting, options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal("user-owned content", File.ReadAllText(userFile));
        Assert.NotEqual(userFile, first.Export.DestinationPath);
        Assert.Equal(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public async Task StaleCrashClaimRecoversAlreadyPublishedHashMatchingFile()
    {
        using var directory = new TestDirectory();
        var meeting = Meeting();
        var markdown = MeetingExportFormatter.BuildMarkdown(meeting, MeetingExportMode.FullMeeting, null);
        var recoveredPath = directory.File("recovered-after-crash.md");
        File.WriteAllText(recoveredPath, markdown, new UTF8Encoding(false));
        var paths = PostMeetingMarkdownAutoExporter.GetControlPaths(
            directory.Path,
            meeting.Id,
            PostMeetingCompletionEvent.RecordingCompleted,
            MeetingExportMode.FullMeeting);
        Directory.CreateDirectory(paths.ControlDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(markdown))).ToLowerInvariant();
        var staleClaim = new PostMeetingMarkdownAutoExporter.ExportClaim(
            1,
            "crashed-owner",
            int.MaxValue,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            Path.GetFileName(recoveredPath),
            hash,
            "Muesli.PostMeetingAutomation");
        File.WriteAllText(
            Path.Combine(paths.ControlDirectory, ".owner.json"),
            "{\"schemaVersion\":1,\"owner\":\"Muesli.PostMeetingAutomation\",\"instanceToken\":\"phase9-test\"}");
        File.WriteAllText(paths.ClaimPath, JsonSerializer.Serialize(staleClaim, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var result = await new PostMeetingAutomationService().RunAsync(
            meeting, AutoExport(directory.Path), PostMeetingCompletionEvent.RecordingCompleted);

        Assert.True(result.Export.Completed, result.Export.Error);
        Assert.Equal(recoveredPath, result.Export.DestinationPath);
        Assert.Equal(0, result.Export.Attempts);
        Assert.Single(Directory.GetFiles(directory.Path, "*.md"));
        Assert.True(File.Exists(paths.ManifestPath));
        Assert.False(File.Exists(paths.ClaimPath));
    }

    [Fact]
    public async Task PersistedSuccessfulExportIsReusedWithoutCreatingACollisionDuplicate()
    {
        using var directory = new TestDirectory();
        var options = AutoExport(directory.Path);
        var service = new PostMeetingAutomationService();
        var first = await service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);
        var reloaded = Meeting(automationResult: first);

        var second = await service.RunAsync(reloaded, options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Single(Directory.GetFiles(directory.Path, "*.md"));
    }

    [Fact]
    public async Task PreviousExportIsReusedOnlyWhenRenderedContentHashMatchesNewMode()
    {
        using var directory = new TestDirectory();
        var service = new PostMeetingAutomationService();
        var notesOptions = AutoExport(directory.Path, MeetingExportMode.Notes);
        var meeting = Meeting();
        var notes = await service.RunAsync(meeting, notesOptions, PostMeetingCompletionEvent.RecordingCompleted);
        var reloaded = Meeting(automationResult: notes);
        var fullOptions = notesOptions with { AutoExportMode = MeetingExportMode.FullMeeting };

        var full = await service.RunAsync(reloaded, fullOptions, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.True(full.Export.Completed, full.Export.Error);
        Assert.NotEqual(notes.Export.DestinationPath, full.Export.DestinationPath);
        Assert.DoesNotContain("## Raw Transcript", File.ReadAllText(notes.Export.DestinationPath!), StringComparison.Ordinal);
        Assert.Contains("## Raw Transcript", File.ReadAllText(full.Export.DestinationPath!), StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public void ImmutableResultRoundTripsThroughJsonForMeetingPersistence()
    {
        var result = new PostMeetingAutomationResult(
            Guid.NewGuid(),
            PostMeetingAutomationStatus.Failed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            2,
            17,
            "bounded output",
            "bounded error",
            false,
            false,
            "failed",
            new PostMeetingExportDiagnostic(true, true, @"C:\exports\meeting.md",
                AutomationDestinationOwnership.UserSelectedDestination, null, 1));

        var roundTrip = JsonSerializer.Deserialize<PostMeetingAutomationResult>(JsonSerializer.Serialize(result));

        Assert.Equal(result, roundTrip);
    }
}
