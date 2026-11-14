using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

public sealed class MeetingEndSuggestionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static readonly DateTimeOffset Closing = Start.AddMinutes(3);
    private static readonly float[] Sound = [0.1f, -0.1f];
    private static readonly float[] Silence = [0, 0];

    private static MeetingEndSuggestionTracker Tracker()
    {
        var tracker = new MeetingEndSuggestionTracker();
        tracker.BeginCapture(Start, newRecording: true);
        return tracker;
    }

    private static LiveTranscriptSnapshot Snapshot(string text, string id = "closing", int queue = 0, long dropped = 0) =>
        new([new(id, LiveTranscriptChannel.System, 0, 16000, text)], "", "", queue, dropped, 0, 0);

    private static void Say(MeetingEndSuggestionTracker tracker, string text, DateTimeOffset? at = null, string id = "closing")
    {
        var now = at ?? Closing;
        tracker.ObserveAudio(Sound, now);
        tracker.ObserveTranscript(Snapshot(text, id), now);
    }

    private static DateTimeOffset Quiet(MeetingEndSuggestionTracker tracker, int seconds = 26, DateTimeOffset? from = null)
    {
        var start = from ?? Closing;
        for (var i = 1; i <= seconds; i++) tracker.ObserveAudio(Silence, start.AddSeconds(i));
        return start.AddSeconds(seconds);
    }

    [Theory]
    [InlineData("That's everything for today. Thanks everyone!")]
    [InlineData("Okay, thanks everyone. See you next week.")]
    [InlineData("Let's call it a day. Have a great weekend!")]
    [InlineData("That’s all for today. Bye bye.")]
    public void ClosingExchangeNeedsSustainedQuiet(string text)
    {
        var tracker = Tracker();
        Say(tracker, text);
        Assert.False(tracker.ShouldSuggest(Quiet(tracker, 20)));
        Assert.True(tracker.ShouldSuggest(Quiet(tracker)));
    }

    [Theory]
    [InlineData("Thanks.")]
    [InlineData("Thanks everyone.")]
    [InlineData("Bye!")]
    [InlineData("Before we wrap up, thanks everyone.")]
    [InlineData("Let's wrap up this topic and move to the budget.")]
    [InlineData("Thanks everyone. One more thing.")]
    [InlineData("Are we done for today? Thanks everyone.")]
    [InlineData("He said goodbye and thanks everyone.")]
    [InlineData("Let's not wrap up. Thanks everyone.")]
    [InlineData("Thanks everyone, see you next week if we finish early.")]
    public void OrdinaryConversationAndSingleGoodbyesDoNotSuggest(string text)
    {
        var tracker = Tracker();
        Say(tracker, text);
        Assert.False(tracker.ShouldSuggest(Quiet(tracker)));
    }

    [Fact]
    public void SeparateClosingUtterancesCombineButContinuingDiscussionCancels()
    {
        var tracker = Tracker();
        Say(tracker, "Thanks everyone.", id: "thanks");
        Say(tracker, "See you next week.", Closing.AddSeconds(2), "bye");
        Assert.True(tracker.ShouldSuggest(Quiet(tracker, from: Closing.AddSeconds(2))));
        Say(tracker, "Actually, we still need to discuss the launch.", Closing.AddSeconds(30), "more");
        Assert.False(tracker.ShouldSuggest(Quiet(tracker, from: Closing.AddSeconds(30))));
    }

    [Fact]
    public void RepeatedSnapshotsDoNotExtendOrRearmClosingEvidence()
    {
        var tracker = Tracker();
        Say(tracker, "Thanks everyone. Goodbye.");
        tracker.ObserveTranscript(Snapshot("Thanks everyone. Goodbye."), Closing.AddSeconds(24));
        Assert.True(tracker.ShouldSuggest(Quiet(tracker)));
        tracker.MarkOffered();
        Assert.False(tracker.CanOffer);
        Say(tracker, "That's all for today. Thanks everyone.", Closing.AddMinutes(2), "again");
        Assert.False(tracker.ShouldSuggest(Quiet(tracker, from: Closing.AddMinutes(2))));
        tracker.BeginCapture(Closing.AddMinutes(3), newRecording: false);
        Assert.False(tracker.CanOffer);
        tracker.BeginCapture(Closing.AddMinutes(4), newRecording: true);
        Assert.True(tracker.CanOffer);
    }

    [Fact]
    public void SpeechAudioAndChangingPartialsDelayTheSuggestion()
    {
        var tracker = Tracker();
        Say(tracker, "Thanks everyone. Goodbye.");
        Quiet(tracker, 24);
        tracker.ObserveAudio(Sound, Closing.AddSeconds(24));
        Assert.False(tracker.ShouldSuggest(Quiet(tracker)));
        tracker.ObserveTranscript(new([], "Let's discuss", "", 0, 0, 0, 0), Closing.AddSeconds(30));
        Assert.False(tracker.ShouldSuggest(Quiet(tracker, 59)));
    }

    [Fact]
    public void StalledAudioAndStaleOrDroppedTranscriptsCannotEstablishAnEnding()
    {
        var tracker = Tracker();
        Say(tracker, "Thanks everyone. Goodbye.");
        Assert.False(tracker.ShouldSuggest(Closing.AddSeconds(26))); // No fresh capture.
        tracker.ObserveAudio(Silence, Closing.AddSeconds(26));
        Assert.False(tracker.ShouldSuggest(Closing.AddSeconds(26))); // Recovery isn't quiet evidence.

        foreach (var snapshot in new[]
        {
            Snapshot("Thanks everyone. Goodbye.", queue: 2),
            Snapshot("Thanks everyone. Goodbye.", dropped: 1)
        })
        {
            tracker = Tracker();
            tracker.ObserveAudio(Sound, Closing);
            tracker.ObserveTranscript(snapshot, Closing);
            Assert.False(tracker.ShouldSuggest(Quiet(tracker)));
        }
        tracker = Tracker();
        tracker.ObserveAudio(Sound, Closing.AddSeconds(-30));
        tracker.ObserveTranscript(Snapshot("Thanks everyone. Goodbye."), Closing);
        Assert.False(tracker.ShouldSuggest(Quiet(tracker)));
    }

    [Fact]
    public void EarlyAndExpiredClosingsDoNotSuggest()
    {
        var tracker = Tracker();
        Say(tracker, "Thanks everyone. Goodbye.", Start.AddSeconds(5));
        Assert.False(tracker.ShouldSuggest(Quiet(tracker, from: Start.AddSeconds(5))));
        Say(tracker, "Thanks everyone. Goodbye.", id: "later");
        Assert.False(tracker.ShouldSuggest(Quiet(tracker, 61)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndPromptRequiresActiveRecordingAndDismissalKeepsItSuppressed(bool sourceMissing)
    {
        var request = MeetingNotificationRequestFactory.EndSuggestion("session", sourceMissing);
        var state = new MeetingNotificationSuppressionState();
        Assert.Equal(MeetingNotificationAction.StopTranscribing, request.SingleAction);
        Assert.False(state.Evaluate(request, false, false, false).Show);
        Assert.False(state.Evaluate(request, false, true, true).Show);
        Assert.True(state.Evaluate(request, false, true, false).Show);
        state.SuppressForAuto(request.PromptId);
        Assert.False(state.Evaluate(request, false, true, false).Show);
        Assert.False(state.Evaluate(MeetingNotificationRequestFactory.EndSuggestion("session", !sourceMissing), false, true, false).Show);
    }

    [Fact]
    public void ReconnectHasOneMinuteGraceAndAudioActivityPostponesSourceWarning()
    {
        var tracker = new MeetingAutoStopTracker(MeetingRecordingStartOrigin.DetectedMeeting, "zoom");
        tracker.Observe("zoom", Start);
        Assert.False(tracker.Observe(null, Start.AddSeconds(3)));
        Assert.False(tracker.Observe(null, Start.AddSeconds(30)));
        Assert.False(tracker.Observe("zoom", Start.AddSeconds(55)));
        Assert.False(tracker.Observe(null, Start.AddSeconds(60)));
        Assert.False(tracker.Observe(null, Start.AddSeconds(90)));
        tracker.NoteTranscriptActivity(Start.AddSeconds(100));
        Assert.False(tracker.Observe(null, Start.AddSeconds(125)));
        Assert.True(tracker.Observe(null, Start.AddSeconds(146)));
    }
}
