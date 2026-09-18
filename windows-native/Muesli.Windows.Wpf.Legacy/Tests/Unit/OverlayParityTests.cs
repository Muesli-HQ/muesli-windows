using Muesli.Windows.Services.Persistence;
using Muesli.Windows.Core.Insights;

namespace Muesli.Windows.Tests;

public sealed class OverlayParityTests
{
    [Fact]
    public void ShortVoicedTapBelow300MsIsSilentDiscardNotErrorClass()
    {
        using var shortVoiced = new CapturedAudio("source.wav", "transcription.wav", [], 4096, 250, 0.2, 0.5f, "mic", "id");
        using var longVoiced = new CapturedAudio("source.wav", "transcription.wav", [], 4096, 400, 0.2, 0.5f, "mic", "id");
        Assert.True(DictationAudioQualityPolicy.IsShortDiscard(shortVoiced));
        Assert.False(DictationAudioQualityPolicy.IsShortDiscard(longVoiced));
        Assert.Equal(300, HotkeyTriggerTiming.ShortDiscardMilliseconds);
    }

    [Fact]
    public void DictionarySuggestionDetectsTokenReplacement()
    {
        var suggestions = DictionarySuggestionDetector.FromEdit("Call muesly tomorrow", "Call Muesli tomorrow");
        var suggestion = Assert.Single(suggestions);
        Assert.Equal("muesly", suggestion.Observed);
        Assert.Equal("Muesli", suggestion.Replacement);
    }

    [Fact]
    public void InsightsAnalyzerIgnoresStopWordsAndRanksRealTokens()
    {
        var words = InsightsWordAnalyzer.TopWords(["The meeting with Priya and Priya about release"]);
        Assert.Contains(words, word => word.Word.Equals("Priya", StringComparison.OrdinalIgnoreCase) && word.Count == 2);
        Assert.DoesNotContain(words, word => word.Word.Equals("the", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProductionSearchMatchesManualNotes()
    {
        Assert.True(ProductionInMemorySearchMatch.MeetingMatches(
            "Standup",
            "summary",
            "transcript",
            "meta",
            "needle-manual",
            "needle-manual"));
        Assert.Contains(
            "needle-manual",
            ProductionInMemorySearchMatch.MeetingSnippet(
                "Standup",
                "summary body",
                "transcript body",
                "keep the needle-manual note",
                "needle-manual"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsightsSnapshotUsesSelectedRangeTotalsAndLocalWordLists()
    {
        var now = new DateTime(2026, 8, 25);
        var snapshot = InsightsWordAnalyzer.Build(
            [
                (now, "Priya Priya shipped the release", 60_000),
                (now.AddDays(-40), "legacy token should drop from thirty days", 60_000)
            ],
            [(now, "Speaker 1: Priya reviewed the release")],
            InsightsRange.ThirtyDays,
            now);
        Assert.Equal(InsightsRange.ThirtyDays, snapshot.Range);
        Assert.True(snapshot.Lifetime.TotalWords > snapshot.Selected.TotalWords);
        Assert.Equal(1, snapshot.CurrentStreakDays);
        Assert.Contains(snapshot.DictationWords, word => word.Word.Equals("Priya", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(snapshot.DictationWords, word => word.Word.Equals("the", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.DailyActivity, day => day.Date == now.Date && day.Intensity > 0.12);
    }

    [Fact]
    public void SearchAndDictionaryViewsExposeRemainingDashboardSurfaces()
    {
        var root = FindRepositoryRoot();
        var search = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Search", "SearchView.xaml"));
        var dictionary = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Dictionary", "DictionaryView.xaml"));
        var meetings = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Meetings.cs"));
        Assert.Contains("SelectSearchDictationsTab_Click", search);
        Assert.Contains("SelectSearchMeetingsTab_Click", search);
        Assert.Contains("SearchShowsDictations", search);
        Assert.Contains("MeetingSearchSnippetConverter", search);
        Assert.Contains("ManualNotes", search);
        Assert.Contains("Suggested Corrections", dictionary);
        Assert.Contains("AcceptDictionarySuggestion_Click", dictionary);
        Assert.Contains("HideLiveTranscriptSoon", meetings);
        Assert.Contains("HoverChanged", File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Services", "ToastNotificationService.cs")));
    }

    [Fact]
    public void MeetingPromptReplacesVisiblePromptInsteadOfDropping()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows", "Services", "MeetingPromptService.cs"));
        Assert.Contains("replacing with the new meeting", source);
        Assert.DoesNotContain("possible stuck state, forcing Reset", source);
    }

    [Fact]
    public void FeatureTourIncludesFloatingIndicatorBeat()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows", "FeatureTourWindow.xaml.cs"));
        Assert.Contains("FloatingIndicator", source);
        Assert.Equal(2, FeatureTourWindow.CurrentVersion);
        Assert.Equal("Middle Right", new MuesliSettings().IndicatorAnchor);
        Assert.Equal(250, new MuesliSettings().HotkeyTriggerThresholdMs);
    }

    [Fact]
    public void OverlayContractsKeepLiveOwnerAndFourDipDrag()
    {
        var toast = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows", "Services", "ToastNotificationService.cs"));
        Assert.Contains("private const double DragThreshold = 4", toast);
        Assert.Contains("ShowLive", toast);
        Assert.Contains("ReleaseLive", toast);
        Assert.Contains("ToastState.Transcribing", toast);
        Assert.Contains("_pauseOrResumeRecording", toast);
        Assert.Contains("Name = \"FloatingIndicator\"", toast);

        var coordinator = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows.Platform", "Services", "DictationCoordinator.cs"));
        Assert.Contains("_cancelRequested", coordinator);
        Assert.Contains("IsShortDiscard", coordinator);
    }

    [Fact]
    public void LiveOwnerBlocksTransientToastsUntilReleased()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var toast = new ToastNotificationService();
                toast.SetIdleIndicatorVisible(false, showNow: false);
                toast.ShowLive(IndicatorOwner.Dictation, "Recording", "Hold", ToastState.Recording);
                Assert.Equal(IndicatorOwner.Dictation, toast.LiveOwner);
                Assert.Equal(ToastState.Recording, toast.CurrentState);
                toast.Show("Pasted", "Transcript delivered", ToastState.Success);
                Assert.Equal(IndicatorOwner.Dictation, toast.LiveOwner);
                Assert.Equal(ToastState.Recording, toast.CurrentState);
                toast.ReleaseLive(IndicatorOwner.Dictation);
                Assert.Equal(IndicatorOwner.None, toast.LiveOwner);
                toast.Show("Copied", "Clipboard", ToastState.Success, 0);
                Assert.Equal(ToastState.Success, toast.CurrentState);
                Assert.Equal(IndicatorOwner.None, toast.LiveOwner);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"Live indicator owner test failed: {failure}");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "windows-native", "Muesli.Windows", "MainWindow.xaml")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Muesli repository root from the test output directory.");
    }
}
