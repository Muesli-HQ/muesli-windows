using System.Text.Json;
using Muesli.Windows.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class Phase3MeetingLifecycleTests
{
    public static IEnumerable<object[]> ValidTransitions()
    {
        yield return Row(MeetingSessionState.Idle, MeetingSessionTrigger.Prepare, MeetingSessionState.Preparing);
        yield return Row(MeetingSessionState.Idle, MeetingSessionTrigger.RestoreInterrupted, MeetingSessionState.RecoverableInterruption);
        yield return Row(MeetingSessionState.Preparing, MeetingSessionTrigger.Prepared, MeetingSessionState.Recording);
        yield return Row(MeetingSessionState.Preparing, MeetingSessionTrigger.PreparedDegraded, MeetingSessionState.DegradedRecording);
        yield return Row(MeetingSessionState.Preparing, MeetingSessionTrigger.Fail, MeetingSessionState.Failed);
        yield return Row(MeetingSessionState.Preparing, MeetingSessionTrigger.Cancel, MeetingSessionState.Cancelled);
        yield return Row(MeetingSessionState.Preparing, MeetingSessionTrigger.Interrupt, MeetingSessionState.RecoverableInterruption);
        yield return Row(MeetingSessionState.Recording, MeetingSessionTrigger.Degrade, MeetingSessionState.DegradedRecording);
        yield return Row(MeetingSessionState.Recording, MeetingSessionTrigger.Stop, MeetingSessionState.Stopping);
        yield return Row(MeetingSessionState.Recording, MeetingSessionTrigger.Interrupt, MeetingSessionState.RecoverableInterruption);
        yield return Row(MeetingSessionState.Recording, MeetingSessionTrigger.Cancel, MeetingSessionState.Cancelled);
        yield return Row(MeetingSessionState.Recording, MeetingSessionTrigger.Fail, MeetingSessionState.Failed);
        yield return Row(MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Recover, MeetingSessionState.Recording);
        yield return Row(MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Stop, MeetingSessionState.Stopping);
        yield return Row(MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Interrupt, MeetingSessionState.RecoverableInterruption);
        yield return Row(MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Cancel, MeetingSessionState.Cancelled);
        yield return Row(MeetingSessionState.DegradedRecording, MeetingSessionTrigger.Fail, MeetingSessionState.Failed);
        yield return Row(MeetingSessionState.Stopping, MeetingSessionTrigger.TracksFinalized, MeetingSessionState.Finalizing);
        yield return Row(MeetingSessionState.Stopping, MeetingSessionTrigger.Interrupt, MeetingSessionState.RecoverableInterruption);
        yield return Row(MeetingSessionState.Stopping, MeetingSessionTrigger.Cancel, MeetingSessionState.Cancelled);
        yield return Row(MeetingSessionState.Stopping, MeetingSessionTrigger.Fail, MeetingSessionState.Failed);
        yield return Row(MeetingSessionState.Finalizing, MeetingSessionTrigger.Complete, MeetingSessionState.Completed);
        yield return Row(MeetingSessionState.Finalizing, MeetingSessionTrigger.Interrupt, MeetingSessionState.RecoverableInterruption);
        yield return Row(MeetingSessionState.Finalizing, MeetingSessionTrigger.Cancel, MeetingSessionState.Cancelled);
        yield return Row(MeetingSessionState.Finalizing, MeetingSessionTrigger.Fail, MeetingSessionState.Failed);
        yield return Row(MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.Resume, MeetingSessionState.Preparing);
        yield return Row(MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.FinalizeInterrupted, MeetingSessionState.Stopping);
        yield return Row(MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.Cancel, MeetingSessionState.Cancelled);
        yield return Row(MeetingSessionState.RecoverableInterruption, MeetingSessionTrigger.Fail, MeetingSessionState.Failed);
        yield return Row(MeetingSessionState.Completed, MeetingSessionTrigger.Reset, MeetingSessionState.Idle);
        yield return Row(MeetingSessionState.Failed, MeetingSessionTrigger.Reset, MeetingSessionState.Idle);
        yield return Row(MeetingSessionState.Failed, MeetingSessionTrigger.Prepare, MeetingSessionState.Preparing);
        yield return Row(MeetingSessionState.Cancelled, MeetingSessionTrigger.Reset, MeetingSessionState.Idle);
    }

    [Theory]
    [MemberData(nameof(ValidTransitions))]
    public void EveryDeclaredMeetingTransitionIsDeterministic(
        MeetingSessionState from,
        MeetingSessionTrigger trigger,
        MeetingSessionState expected)
    {
        var machine = MachineAt(from);

        var transition = machine.Apply(trigger, "test\nreason", DateTimeOffset.UnixEpoch);

        Assert.Equal(from, transition.From);
        Assert.Equal(expected, transition.To);
        Assert.Equal(expected, machine.State);
        Assert.Equal("test reason", transition.Reason);
        Assert.Equal(DateTimeOffset.UnixEpoch, transition.Timestamp);
    }

    [Theory]
    [InlineData(MeetingSessionState.Idle, MeetingSessionTrigger.Stop)]
    [InlineData(MeetingSessionState.Recording, MeetingSessionTrigger.Complete)]
    [InlineData(MeetingSessionState.Completed, MeetingSessionTrigger.Resume)]
    [InlineData(MeetingSessionState.Cancelled, MeetingSessionTrigger.FinalizeInterrupted)]
    public void InvalidTransitionsFailWithoutChangingState(
        MeetingSessionState state,
        MeetingSessionTrigger trigger)
    {
        var machine = MachineAt(state);

        Assert.Throws<InvalidOperationException>(() => machine.Apply(trigger));
        Assert.Equal(state, machine.State);
        Assert.False(machine.TryApply(trigger, out _));
    }

    [Fact]
    public void TransitionHistoryIsBoundedAndContainsNoMultilineReason()
    {
        var machine = new MeetingSessionStateMachine();
        for (var index = 0; index < 40; index++)
        {
            machine.Apply(MeetingSessionTrigger.Prepare, new string('x', 300) + "\nsecret");
            machine.Apply(MeetingSessionTrigger.Cancel);
            machine.Apply(MeetingSessionTrigger.Reset);
        }

        Assert.Equal(64, machine.History.Count);
        Assert.All(machine.History, item =>
        {
            Assert.DoesNotContain('\n', item.Reason);
            Assert.True(item.Reason.Length <= 240);
        });
    }

    [Fact]
    public void MissingSilentClippingAndRecoveryAreDistinguished()
    {
        var started = DateTimeOffset.UnixEpoch;
        var monitor = new MeetingAudioHealthMonitor(
            started,
            missingAfter: TimeSpan.FromSeconds(3),
            silentAfter: TimeSpan.FromSeconds(4),
            callbackFreshness: TimeSpan.FromSeconds(2),
            clippingAfter: TimeSpan.FromSeconds(2));

        monitor.Note(Metrics(MeetingAudioChannel.System, started.AddSeconds(2), 0.4f));
        var missing = monitor.Evaluate(started.AddSeconds(4));
        Assert.Equal(MeetingChannelHealth.Missing, missing.Microphone.Health);

        monitor.Note(Metrics(MeetingAudioChannel.Microphone, started.AddSeconds(4), 0));
        monitor.Note(Metrics(MeetingAudioChannel.System, started.AddSeconds(7), 0.4f));
        monitor.Note(Metrics(MeetingAudioChannel.Microphone, started.AddSeconds(7), 0));
        var silent = monitor.Evaluate(started.AddSeconds(8));
        Assert.Equal(MeetingChannelHealth.Silent, silent.Microphone.Health);

        monitor.Note(Metrics(MeetingAudioChannel.Microphone, started.AddSeconds(9), 1, clipped: 100));
        monitor.Note(Metrics(MeetingAudioChannel.Microphone, started.AddSeconds(11), 1, clipped: 100));
        var clipping = monitor.Evaluate(started.AddSeconds(11));
        Assert.Equal(MeetingChannelHealth.Clipping, clipping.Microphone.Health);

        var recovered = monitor.Note(Metrics(MeetingAudioChannel.Microphone, started.AddSeconds(12), 0.2f));
        Assert.Equal(MeetingChannelHealth.Healthy, recovered.Microphone.Health);
    }

    [Fact]
    public void SilenceWithoutPeerActivityDoesNotCreateAWarning()
    {
        var started = DateTimeOffset.UnixEpoch;
        var monitor = new MeetingAudioHealthMonitor(
            started,
            missingAfter: TimeSpan.FromSeconds(1),
            silentAfter: TimeSpan.FromSeconds(1));
        monitor.Note(Metrics(MeetingAudioChannel.Microphone, started, 0));

        var snapshot = monitor.Evaluate(started.AddMinutes(1));

        Assert.Equal(MeetingChannelHealth.Waiting, snapshot.Microphone.Health);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public void RepairPolicyUsesBoundedBackoffAndPreservesWhenBothChannelsFail()
    {
        Assert.Equal(MeetingCaptureRepairAction.RestartNow, MeetingCaptureRepairPolicy.Decide(0, true).Action);
        Assert.Equal(TimeSpan.FromSeconds(2), MeetingCaptureRepairPolicy.Decide(1, true).Delay);
        Assert.Equal(TimeSpan.FromSeconds(5), MeetingCaptureRepairPolicy.Decide(2, true).Delay);
        Assert.Equal(MeetingCaptureRepairAction.ContinueDegraded,
            MeetingCaptureRepairPolicy.Decide(MeetingCaptureRepairPolicy.MaximumAttempts, true).Action);
        Assert.Equal(MeetingCaptureRepairAction.PreserveForRecovery,
            MeetingCaptureRepairPolicy.Decide(MeetingCaptureRepairPolicy.MaximumAttempts, false).Action);
        Assert.Throws<ArgumentOutOfRangeException>(() => MeetingCaptureRepairPolicy.Decide(-1, true));
    }

    [Fact]
    public void AutoStopIsDisabledForManualMeetingsAndConservativeForDetectedMeetings()
    {
        var now = DateTimeOffset.UnixEpoch;
        var manual = new MeetingAutoStopTracker(MeetingRecordingStartOrigin.Manual, "meeting");
        Assert.False(manual.IsArmed);
        Assert.False(manual.Observe(null, now.AddHours(1)));

        var detected = new MeetingAutoStopTracker(
            MeetingRecordingStartOrigin.DetectedMeeting,
            "meeting-a",
            TimeSpan.FromSeconds(20),
            minimumMissingObservations: 3);
        Assert.False(detected.Observe(null, now.AddMinutes(1)));
        Assert.False(detected.Observe("meeting-b", now.AddMinutes(2)));
        Assert.False(detected.Observe("meeting-a", now));
        Assert.False(detected.Observe(null, now.AddSeconds(1)));
        Assert.False(detected.Observe(null, now.AddSeconds(19)));
        Assert.True(detected.Observe(null, now.AddSeconds(21)));
        Assert.False(detected.Observe("meeting-a", now.AddSeconds(22)));
    }

    [Fact]
    public void EndpointPoliciesCoverDefaultDeviceRemovalAndBluetoothStyleUnplug()
    {
        Assert.True(SystemAudioRouteRecoveryPolicy.ShouldRotate(
            "render-one",
            new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, "render-two")));
        Assert.True(SystemAudioRouteRecoveryPolicy.ShouldRotate(
            "bluetooth-render",
            new AudioEndpointChange(AudioEndpointChangeKind.StateChanged, "bluetooth-render", DeviceState.Unplugged)));
        Assert.True(SystemAudioRouteRecoveryPolicy.ShouldRotate(
            "render-one",
            new AudioEndpointChange(AudioEndpointChangeKind.Removed, "render-one")));
        Assert.False(SystemAudioRouteRecoveryPolicy.ShouldRotate(
            "render-one",
            new AudioEndpointChange(AudioEndpointChangeKind.Added, "render-two")));
    }

    [Fact]
    public void AudioMetricsDetectSignalAndClipping()
    {
        var samples = new short[] { 0, 1000, short.MaxValue, short.MinValue };
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var metrics = AudioBufferMetrics.Measure(
            MeetingAudioChannel.Microphone,
            bytes,
            bytes.Length,
            new WaveFormat(16000, 16, 1),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(samples.Length, metrics.SampleCount);
        Assert.Equal(2, metrics.ClippedSampleCount);
        Assert.True(metrics.Rms > 0);
        Assert.Equal(1f, metrics.Peak);
    }

    [Fact]
    public void InterruptedLiveWavIsRepairedAdoptedAndPromotedToRetainedTracks()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create(
            "meet_recovery",
            "Recovery",
            DateTimeOffset.UnixEpoch,
            "System default microphone",
            "parakeet-v3",
            retainRecording: true,
            targetProcessId: null);
        var live = Path.Combine(
            store.GetSessionDirectory(journal.SessionId),
            $"{MeetingSessionJournalStore.MicrophoneCapturePrefix}crashed.wav");
        WritePcmWav(live, Enumerable.Repeat((short)1200, 3200).ToArray());
        CorruptWavSizes(live);

        var recoverable = Assert.Single(store.DiscoverRecoverable());

        Assert.Equal(MeetingSessionState.RecoverableInterruption, recoverable.Journal.State);
        Assert.Equal(1, recoverable.MicrophonePartCount);
        Assert.Contains(recoverable.RecoveryWarnings, warning => warning.Contains("header was repaired"));
        var paths = store.BuildFinalTracks(recoverable.Journal);
        Assert.NotNull(paths.MicrophonePath);
        Assert.True(File.Exists(paths.MicrophonePath));
        Assert.EndsWith("microphone.wav", paths.MicrophonePath, StringComparison.OrdinalIgnoreCase);
        using var reader = new AudioFileReader(paths.MicrophonePath!);
        Assert.True(reader.TotalTime > TimeSpan.Zero);

        store.DeleteSession(journal.SessionId);
        Assert.True(File.Exists(paths.MicrophonePath));
    }

    [Fact]
    public void ZeroLengthInterruptedTrackIsDiscardedWithoutFakeRecovery()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create(
            "meet_empty",
            "Empty",
            DateTimeOffset.UnixEpoch,
            "default",
            "parakeet-v3",
            true,
            null);
        File.WriteAllBytes(Path.Combine(
            store.GetSessionDirectory(journal.SessionId),
            $"{MeetingSessionJournalStore.SystemCapturePrefix}empty.wav"), new byte[44]);

        Assert.Empty(store.DiscoverRecoverable());
    }

    [Fact]
    public void HeaderOnlyOrTooShortTrackNeverReachesFinalization()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create(
            "meet_too_short",
            "Too short",
            DateTimeOffset.UnixEpoch,
            "default",
            "parakeet-v3",
            true,
            null);
        var headerOnly = directory.File("header-only.wav");
        WritePcmWav(headerOnly, [(short)0]);

        journal = store.AppendPart(journal, MeetingAudioChannel.System, headerOnly);
        var paths = store.BuildFinalTracks(journal);

        Assert.Empty(journal.SystemParts);
        Assert.Null(paths.SystemPath);
    }

    [Fact]
    public void FinalizedAudioRemainsRecoverableUntilMeetingPersistenceIsAcknowledged()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create(
            "meet_uncommitted",
            "Uncommitted",
            DateTimeOffset.UnixEpoch,
            "default",
            "parakeet-v3",
            true,
            null);
        var source = Path.Combine(directory.Path, "source.wav");
        WritePcmWav(source, Enumerable.Repeat((short)800, 1600).ToArray());
        journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, source) with
        {
            State = MeetingSessionState.Finalizing
        };
        store.Save(journal);

        var recoverable = Assert.Single(store.DiscoverRecoverable());

        Assert.True(recoverable.Journal.RecoveredFromInterruption);
        Assert.Equal(MeetingSessionState.RecoverableInterruption, recoverable.Journal.State);
        Assert.Equal(1, recoverable.MicrophonePartCount);
    }

    [Fact]
    public void JournalRejectsPathTraversalDuringFinalTrackBuild()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create(
            "meet_safe",
            "Safe",
            DateTimeOffset.UnixEpoch,
            "default",
            "parakeet-v3",
            false,
            null) with
        {
            MicrophoneParts = ["..\\outside.wav"]
        };

        Assert.Throws<InvalidDataException>(() => store.BuildFinalTracks(journal));
    }

    [Fact]
    public void LegacyMeetingRecordMigratesWithoutLosingAudioOrCompletedStatus()
    {
        using var directory = new TestDirectory();
        var dataPath = directory.File("windows-meetings.json");
        var legacyAudio = directory.File("microphone.wav");
        File.WriteAllBytes(legacyAudio, [1, 2, 3]);
        File.WriteAllText(dataPath, JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = "legacy",
                Title = "Legacy",
                CreatedAt = DateTime.UnixEpoch,
                DurationMs = 1000,
                Transcript = "retained text",
                Summary = "",
                SourcePath = legacyAudio,
                ModelProfile = "parakeet-v3"
            }
        }));
        var store = new AppDataStore(directory.Path);

        var meeting = Assert.Single(store.LoadMeetings());

        Assert.Equal(AppDataStore.CurrentMeetingSchemaVersion, meeting.SchemaVersion);
        Assert.Equal(MeetingSessionState.Completed, meeting.SessionState);
        Assert.Equal(legacyAudio, meeting.MicrophoneAudioPath);
        Assert.Equal("retained text", meeting.Transcript);
    }

    [Fact]
    public void PlaybackTrackSelectionKeepsSeparateExistingChannelsAndRejectsMissingPaths()
    {
        using var directory = new TestDirectory();
        var mic = directory.File("microphone.wav");
        var system = directory.File("system.wav");
        File.WriteAllBytes(mic, [1]);
        File.WriteAllBytes(system, [2]);

        var tracks = MeetingRecordingPlaybackService.SelectTracks(mic, system, "missing.wav");

        Assert.Collection(
            tracks,
            track => Assert.Equal("Microphone (You)", track.Label),
            track => Assert.Equal("Meeting audio (Others)", track.Label));
    }

    [Fact]
    public void ProcessLoopbackCapabilityIsBuildAndTargetGated()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var oldBuild = WindowsProcessLoopbackSupport.Inspect(
            process.Id,
            new Version(10, 0, WindowsProcessLoopbackSupport.MinimumWindowsBuild - 1));
        var currentBuild = WindowsProcessLoopbackSupport.Inspect(
            process.Id,
            new Version(10, 0, WindowsProcessLoopbackSupport.MinimumWindowsBuild));
        var missingTarget = WindowsProcessLoopbackSupport.Inspect(
            null,
            new Version(10, 0, WindowsProcessLoopbackSupport.MinimumWindowsBuild));

        Assert.False(oldBuild.CanAttempt);
        Assert.True(currentBuild.CanAttempt);
        Assert.False(missingTarget.CanAttempt);
    }

    [Fact]
    public void SessionDiagnosticsKeepOnlyOperationalWarnings()
    {
        var warnings = MeetingRecordingCoordinator.CleanupHealthWarnings(
            [
                "Microphone was silent while remote audio was active.",
                "Input path: C:\\Users\\someone\\meeting.wav",
                "This sentence could be transcript content.",
                "Meeting capture was interrupted."
            ],
            transcript: "private transcript text",
            diarizationSucceededWithSegments: false);

        Assert.Equal(
            [
                "Microphone was silent while remote audio was active.",
                "Meeting capture was interrupted."
            ],
            warnings);
    }

    [Fact]
    public void PlaybackTrackRendersItsUserFacingLabel()
    {
        var track = new MeetingPlaybackTrack("Microphone (You)", "microphone.wav");

        Assert.Equal("Microphone (You)", track.ToString());
    }

    private static object[] Row(
        MeetingSessionState from,
        MeetingSessionTrigger trigger,
        MeetingSessionState to) => [from, trigger, to];

    private static MeetingSessionStateMachine MachineAt(MeetingSessionState state)
    {
        var machine = new MeetingSessionStateMachine();
        switch (state)
        {
            case MeetingSessionState.Idle:
                return machine;
            case MeetingSessionState.Preparing:
                machine.Apply(MeetingSessionTrigger.Prepare);
                break;
            case MeetingSessionState.Recording:
                machine.Apply(MeetingSessionTrigger.Prepare);
                machine.Apply(MeetingSessionTrigger.Prepared);
                break;
            case MeetingSessionState.DegradedRecording:
                machine.Apply(MeetingSessionTrigger.Prepare);
                machine.Apply(MeetingSessionTrigger.PreparedDegraded);
                break;
            case MeetingSessionState.Stopping:
                machine.Apply(MeetingSessionTrigger.Prepare);
                machine.Apply(MeetingSessionTrigger.Prepared);
                machine.Apply(MeetingSessionTrigger.Stop);
                break;
            case MeetingSessionState.Finalizing:
                machine.Apply(MeetingSessionTrigger.Prepare);
                machine.Apply(MeetingSessionTrigger.Prepared);
                machine.Apply(MeetingSessionTrigger.Stop);
                machine.Apply(MeetingSessionTrigger.TracksFinalized);
                break;
            case MeetingSessionState.Completed:
                machine.Apply(MeetingSessionTrigger.Prepare);
                machine.Apply(MeetingSessionTrigger.Prepared);
                machine.Apply(MeetingSessionTrigger.Stop);
                machine.Apply(MeetingSessionTrigger.TracksFinalized);
                machine.Apply(MeetingSessionTrigger.Complete);
                break;
            case MeetingSessionState.Failed:
                machine.Apply(MeetingSessionTrigger.Prepare);
                machine.Apply(MeetingSessionTrigger.Fail);
                break;
            case MeetingSessionState.Cancelled:
                machine.Apply(MeetingSessionTrigger.Prepare);
                machine.Apply(MeetingSessionTrigger.Cancel);
                break;
            case MeetingSessionState.RecoverableInterruption:
                machine.Apply(MeetingSessionTrigger.RestoreInterrupted);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
        return machine;
    }

    private static MeetingAudioMetrics Metrics(
        MeetingAudioChannel channel,
        DateTimeOffset timestamp,
        float peak,
        int clipped = 0) => new(channel, timestamp, 100, peak / 2, peak, clipped);

    private static void WritePcmWav(string path, short[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
    }

    private static void CorruptWavSizes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = 4;
        stream.Write(new byte[4]);
    }
}
