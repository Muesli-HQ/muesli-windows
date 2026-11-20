using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

// Ported from macOS LiveDictationTextTargetTests.swift (keyboard transport) and LiveDictationKeyboardInputTests.swift.
public sealed class LiveDictationTextTargetTests
{
    private static readonly LiveDictationTextTarget.ReadbackTiming FastTiming = new()
    {
        Timeout = TimeSpan.FromMilliseconds(100), AvailabilityTimeout = TimeSpan.FromMilliseconds(200), Wait = () => Task.Delay(1)
    };

    [Fact]
    public async Task KeyboardAppendPreservesPrefixAndTypesOnlyNewSuffix()
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.True(await target.UpdateAsync("", "hello"));
        editor.Steps.Clear();
        Assert.True(await target.UpdateAsync("", "hello world"));
        Assert.Equal(" world", string.Concat(editor.Steps));
        Assert.Equal(new LiveTextRange(18, 0), editor.Selection);
        Assert.Equal("Before hello world after", editor.Text);
        Assert.Equal(0, editor.Selections);
    }

    [Fact]
    public async Task KeyboardRevisionAndShorteningRetypeOneRetainedGraphemeWithoutDeletes()
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.True(await target.UpdateAsync("", "hello wrong"));
        editor.Steps.Clear();
        Assert.True(await target.UpdateAsync("", "hello world"));
        Assert.Equal("orld", string.Concat(editor.Steps));
        editor.Steps.Clear();
        Assert.True(await target.FinishAsync("hello"));
        Assert.Equal(["o"], editor.Steps);
        Assert.Equal(new LiveTextRange(11, 7), editor.SelectedRanges[^1]);
        Assert.Equal("Before hello after", editor.Text);
    }

    [Fact]
    public async Task StructuredPreviewsFreezeCommittedPrefix()
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.True(await target.UpdateAsync("Hello ", "wrld"));
        editor.SelectedRanges.Clear();
        Assert.True(await target.UpdateAsync("Hello ", "world"));
        Assert.True(await target.UpdateAsync("Hello world", " again"));
        Assert.Equal("Before Hello world again after", editor.Text);
        Assert.All(editor.SelectedRanges, range => Assert.True(range.Location >= 13));
        var selections = editor.Selections;
        Assert.False(await target.UpdateAsync("HELLO world", " again"));
        Assert.True(target.IsSuspended);
        Assert.Equal(selections, editor.Selections);
    }

    [Fact]
    public async Task FinalCleanupCanReviseFrozenCommittedText()
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.True(await target.UpdateAsync("hello ", "world"));
        Assert.True(await target.FinishAsync("Hello, world. "));
        Assert.Equal("Before Hello, world.  after", editor.Text);
        Assert.False(target.IsSuspended);
    }

    [Fact]
    public async Task ClearingTailNeverRewritesFrozenKeyboardGrapheme()
    {
        var editor = new Editor();
        var diagnostics = new List<string>();
        var target = editor.Target(diagnostics.Add);
        Assert.True(await target.UpdateAsync("Stable", " tail"));
        var selections = editor.Selections;
        Assert.True(await target.UpdateAsync("Stable", ""));
        Assert.Equal(selections, editor.Selections);
        Assert.Equal("Before Stable tail after", editor.Text);
        Assert.Equal("preview_deferred_committed_boundary", diagnostics[^1]);
        Assert.True(await target.UpdateAsync("Stable", " text"));
        Assert.Equal("Before Stable text after", editor.Text);
        Assert.False(target.IsSuspended);
        target.Cancel();
    }

    [Fact]
    public async Task LongLiveRewriteIsDeferredWithoutAdvancingCommitment()
    {
        var editor = new Editor();
        var diagnostics = new List<string>();
        var target = editor.Target(diagnostics.Add);
        var tail = new string('a', 81);
        // Initial append remains unrestricted even when it exceeds the cap.
        Assert.True(await target.UpdateAsync("Stable ", tail));
        var selections = editor.Selections;
        Assert.True(await target.UpdateAsync("Stable changed ", "replacement"));
        Assert.Equal(selections, editor.Selections);
        Assert.Equal("Before Stable " + tail + " after", editor.Text);
        Assert.Equal("preview_deferred_rewrite_limit", diagnostics[^1]);
        // The deferred incoming commitment was never frozen.
        Assert.True(await target.UpdateAsync("Stable ", tail + " append"));
        Assert.Equal("Before Stable " + tail + " append after", editor.Text);
        Assert.True(await target.FinishAsync("A clean final sentence. "));
        Assert.Equal("Before A clean final sentence.  after", editor.Text);
    }

    [Fact]
    public async Task StructuredCommitmentsUseWholeGraphemeBoundaries()
    {
        var editor = new Editor();
        Assert.False(await editor.Target().UpdateAsync("e", "́"));
        Assert.Equal(0, editor.Selections);
        Assert.Empty(editor.Steps);
    }

    [Theory]
    [InlineData("safe\nsubmit")]
    [InlineData("safe\tother")]
    [InlineData("safe\rsubmit")]
    [InlineData("safe\u001B")]
    [InlineData("safe\u007F")]
    [InlineData("safe")]
    public async Task KeyboardPreflightsAllControlsBeforeAnyMutation(string value)
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.False(await target.UpdateAsync("", value));
        Assert.False(target.HasAttemptedWrite);
        Assert.Equal(0, editor.Selections);
        Assert.Empty(editor.Steps);
    }

    [Fact]
    public async Task MultilineKeyboardFinalPreservesAcknowledgedPreview()
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.True(await target.UpdateAsync("", "preview"));
        var steps = editor.Steps.ToList();
        Assert.False(await target.FinishAsync("Cleaned\nparagraph"));
        Assert.Equal("Before preview after", editor.Text);
        Assert.Equal(steps, editor.Steps);
    }

    [Fact]
    public async Task KeyboardDispatchFailurePreservesAcknowledgedPrefixAndStops()
    {
        var editor = new Editor { FailAtStep = 2 };
        var target = editor.Target();
        Assert.False(await target.UpdateAsync("", "he llo"));
        Assert.Equal("Before he after", editor.Text);
        Assert.True(target.HasInsertedText);
        Assert.True(target.IsSuspended);
        Assert.False(await target.FinishAsync("final"));
        Assert.Equal("Before he after", editor.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserEditOrCaretMoveStopsWrites(bool changesText)
    {
        var editor = new Editor();
        var suspended = 0;
        var target = editor.Target();
        target.OnSuspended = () => suspended++;
        Assert.True(await target.UpdateAsync("", "first"));
        if (changesText) editor.Text += "user edit"; else editor.Selection = new LiveTextRange(0, 0);
        var before = editor.Text;
        Assert.False(await target.UpdateAsync("", "first overwrite"));
        Assert.True(target.IsSuspended);
        Assert.Equal(before, editor.Text);
        Assert.Equal(1, suspended);
    }

    [Fact]
    public async Task FocusLossPermanentlySuspends()
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.True(await target.UpdateAsync("", "first"));
        editor.Status = LiveDictationFocusStatus.Departed("focus_field_changed");
        Assert.False(await target.UpdateAsync("", "first wrong app"));
        editor.Status = LiveDictationFocusStatus.Focused;
        Assert.False(await target.FinishAsync("back again"));
        Assert.True(target.IsSuspended);
        Assert.Equal("Before first after", editor.Text);
    }

    [Fact]
    public async Task TransientUnavailabilityPausesWithoutSuspending()
    {
        var editor = new Editor { Status = LiveDictationFocusStatus.Unavailable("focused_element_error") };
        var target = editor.Target();
        // macOS: a paused preview is deferred, not a failure, and the next preview continues.
        Assert.True(await target.UpdateAsync("", "hello"));
        Assert.True(target.IsPaused);
        Assert.False(target.IsSuspended);
        editor.Status = LiveDictationFocusStatus.Focused;
        Assert.True(await target.UpdateAsync("", "hello"));
        Assert.Equal("Before hello after", editor.Text);
    }

    [Fact]
    public async Task OwnDelayedNotificationsDoNotSuspendButUserEditsDo()
    {
        var editor = new Editor();
        var target = editor.Target();
        Assert.True(await target.UpdateAsync("", "hello"));
        target.TextTargetDidChange();
        await Task.Delay(50);
        Assert.False(target.IsSuspended);
        editor.Text = "Before hello! after";
        target.TextTargetDidChange();
        await Task.Delay(50);
        Assert.True(target.IsSuspended);
    }

    [Fact]
    public void AcknowledgmentGroupsIsolateFirstGraphemeSeparatorsAndBoundWords()
    {
        Assert.Equal(["h", "ello", " ", "abcdefgh", "ij", ",", " ", "👩🏽‍💻"],
            LiveDictationKeyboardInput.AcknowledgmentGroups("hello abcdefghij, 👩🏽‍💻"));
        Assert.Null(LiveDictationKeyboardInput.AcknowledgmentGroups("a\nb"));
    }

    private sealed class Editor
    {
        public string Text = "Before  after";
        public LiveTextRange Selection = new(7, 0);
        public LiveDictationFocusStatus Status = LiveDictationFocusStatus.Focused;
        public int? FailAtStep;
        public int Selections;
        public List<LiveTextRange> SelectedRanges { get; } = [];
        public List<string> Steps { get; } = [];

        private LiveDictationSnapshot? Read() =>
            Status.Kind == LiveDictationFocusStatus.Kinds.Focused ? new LiveDictationSnapshot(Text, Selection) : null;

        public LiveDictationTextTarget Target(Action<string>? diagnostic = null) =>
            LiveDictationTextTarget.Create(new LiveDictationTextAccess
            {
                Snapshot = () => Task.FromResult(Read()),
                SnapshotNow = Read,
                FocusStatus = () => Task.FromResult(Status),
                CurrentProcessStatus = () => LiveDictationFocusStatus.Focused,
                Select = (range, expected) =>
                {
                    if (Read() != expected) return Task.FromResult(false);
                    Selections++;
                    SelectedRanges.Add(range);
                    Selection = range;
                    return Task.FromResult(true);
                },
                SendBurst = (text, validate, onFailure) =>
                {
                    if (Steps.Count == FailAtStep) { onFailure("target_validation_failed"); return false; }
                    if (!validate()) return false;
                    Steps.Add(text);
                    Text = string.Concat(Text.AsSpan(0, Selection.Location), text, Text.AsSpan(Selection.End));
                    Selection = new LiveTextRange(Selection.Location + text.Length, 0);
                    return true;
                }
            }, new LiveDictationSnapshot(Text, Selection), diagnostic, FastTiming)!;
    }
}
