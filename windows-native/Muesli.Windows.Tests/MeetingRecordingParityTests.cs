using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// macOS parity for a meeting in progress: the waveform follows the captured audio without live
/// transcription, the pill never says Transcribing while a meeting starts, the recording has its
/// own page, and detection stays quiet while Muesli itself uses the microphone.
/// </summary>
public sealed class MeetingRecordingParityTests
{
    [Fact]
    public void WaveformLevelFollowsEitherChannelAndFallsSilentWhenBuffersStop()
    {
        long now = 0;
        var meter = new MeetingLevelMeter(() => now, ticksPerSecond: 1000);
        Assert.Equal(0f, meter.Current);

        meter.Note(MeetingAudioChannel.Microphone, 0.2);
        Assert.Equal(0.2f, meter.Current, 3);

        // The other side of the call is louder: the waveform shows it.
        now = 100;
        meter.Note(MeetingAudioChannel.System, 0.6);
        Assert.Equal(0.6f, meter.Current, 3);
        Assert.Equal(0.2f, meter.Microphone, 3);

        // Windows delivers no loopback buffers while nothing plays; the last level must not linger.
        now = 100 + (long)MeetingLevelMeter.StaleAfter.TotalMilliseconds + 1;
        meter.Note(MeetingAudioChannel.Microphone, 0.1);
        Assert.Equal(0f, meter.SystemAudio);
        Assert.Equal(0.1f, meter.Current, 3);

        now += 1000;
        Assert.Equal(0f, meter.Current);

        meter.Note(MeetingAudioChannel.Microphone, double.NaN);
        Assert.Equal(0f, meter.Current);
        meter.Note(MeetingAudioChannel.Microphone, 7);
        Assert.Equal(1f, meter.Current);
    }

    [Fact]
    public void CaptureLevelsAreFedFromAudioBuffersNotFromLiveTranscriptSnapshots()
    {
        var capture = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.Platform", "Services", "MeetingCaptureSession.cs"));
        Assert.Contains("Levels.Note(metrics.Channel, metrics.Rms);", capture, StringComparison.Ordinal);

        var host = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Services", "WinUiIndicatorHost.cs"));
        Assert.Contains("new FloatingIndicatorWindow(OnCommand, LiveLevel, OnWindowFailure)", host, StringComparison.Ordinal);
        Assert.Contains("_meetings.CurrentLevel", host, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.MicrophoneLevel", File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Services", "WinUiMeetingContext.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void StartingAMeetingShowsPreparingAndOnlyFinishedAudioShowsTranscribing()
    {
        // Start requested, capture not live yet (the meeting context is busy, but not transcribing).
        Assert.Equal(FloatingIndicatorState.Preparing,
            FloatingIndicatorStateClassifier.ClassifyMeeting(isRecording: false, isPaused: false, isStarting: true, isBusy: true));
        Assert.Equal(FloatingIndicatorState.Recording,
            FloatingIndicatorStateClassifier.ClassifyMeeting(isRecording: true, isPaused: false, isStarting: false, isBusy: false));
        Assert.Equal(FloatingIndicatorState.Recording,
            FloatingIndicatorStateClassifier.ClassifyMeeting(isRecording: false, isPaused: true, isStarting: false, isBusy: false));
        // Stop, recovery and import work on audio that already exists.
        Assert.Equal(FloatingIndicatorState.Transcribing,
            FloatingIndicatorStateClassifier.ClassifyMeeting(isRecording: false, isPaused: false, isStarting: false, isBusy: true));
        Assert.Null(FloatingIndicatorStateClassifier.ClassifyMeeting(false, false, false, false));
    }

    [Fact]
    public void TheMeetingInProgressHasItsOwnPageAndTheListOnlyLinksToIt()
    {
        var winUi = TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI");
        var recording = File.ReadAllText(Path.Combine(winUi, "Pages", "MeetingRecordingPage.xaml"));
        foreach (var id in new[] { "MeetingRecordingBackButton", "MeetingRecordingTitle", "MeetingRecordingElapsed", "PauseMeetingButton",
                     "StopMeetingButton", "DiscardMeetingButton", "MeetingCaptureStatusLabel", "MeetingRecordingNotesEditor", "MeetingLiveCaptionList" })
        {
            Assert.Contains(id, recording, StringComparison.Ordinal);
        }

        var list = File.ReadAllText(Path.Combine(winUi, "Pages", "MeetingsPage.xaml"));
        Assert.Contains("OpenRecordingNotesButton", list, StringComparison.Ordinal);
        Assert.DoesNotContain("MeetingRecordingNotesEditor", list, StringComparison.Ordinal);
        Assert.DoesNotContain("MeetingLiveCaptionList", list, StringComparison.Ordinal);
        Assert.DoesNotContain("MeetingCaptureStatusLabel", list, StringComparison.Ordinal);

        var shell = File.ReadAllText(Path.Combine(winUi, "MainPage.xaml.cs"));
        Assert.Contains("typeof(Pages.MeetingRecordingPage)", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionIsHeldWhileMuesliUsesTheMicrophoneAndForTheCooldownAfter()
    {
        var suppression = new MeetingDetectionSuppression();
        var start = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        Assert.False(suppression.IsSuppressed(start));

        suppression.SuppressWhileActive();
        Assert.True(suppression.IsSuppressed(start.AddHours(1)));

        suppression.ResumeAfterCooldown(start);
        Assert.True(suppression.IsSuppressed(start.AddSeconds(14)));
        Assert.False(suppression.IsSuppressed(start + MeetingDetectionSuppression.Cooldown));

        // Resuming without an active suppression does not start a cooldown.
        suppression.ResumeAfterCooldown(start.AddMinutes(5));
        Assert.False(suppression.IsSuppressed(start.AddMinutes(5)));
    }

    [Fact]
    public void AMeetingWhosePromptTimedOutCanBeOfferedOnceMoreButNotAfterTheUserDismissedIt()
    {
        var resolver = new MeetingCandidateResolver();
        var snapshot = new MeetingPresenceSnapshot(MeetingEvidenceStrength.Strong, true, false, "google meet|chrome|https://meet.google.com/abc-defg-hij");

        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(snapshot).Action);
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(snapshot).Action);
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(snapshot).Action);

        resolver.Reprompt(snapshot.CandidateKey);
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(snapshot).Action);
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(snapshot).Action);

        resolver.Dismiss(snapshot.CandidateKey);
        resolver.Reprompt(snapshot.CandidateKey);
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(snapshot).Action);
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(snapshot).Action);
    }
}
