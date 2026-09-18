using System.Windows.Input;

namespace Muesli.Windows.Tests;

public sealed class HotkeyAndPasteTests
{
    [Fact]
    public void NativeInputLayoutMatchesWindowsAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, ActiveAppPasteService.NativeInputStructureSize);
    }

    [Theory]
    [InlineData("F8", Key.F8, ModifierKeys.None)]
    [InlineData("control + shift + space", Key.Space, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("Win+Alt+D", Key.D, ModifierKeys.Windows | ModifierKeys.Alt)]
    public void ParsesAndNormalizesHotkeys(string input, Key key, ModifierKeys modifiers)
    {
        var gesture = HotkeyGesture.Parse(input);
        Assert.Equal(key, gesture.Key);
        Assert.Equal(modifiers, gesture.Modifiers);
    }

    [Fact]
    public void ExactModifierMatchingRejectsUnexpectedExtras()
    {
        Assert.True(HotkeyGesture.Parse("F8").Matches(Key.F8, ModifierKeys.None));
        Assert.False(HotkeyGesture.Parse("F8").Matches(Key.F8, ModifierKeys.Control));
        Assert.True(HotkeyGesture.Parse("Ctrl+F8").Matches(Key.F8, ModifierKeys.Control));
        Assert.False(HotkeyGesture.Parse("Ctrl+F8").Matches(Key.F8, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Throws<InvalidOperationException>(() => HotkeyGesture.Parse("Ctrl+F8+D"));
    }

    [Fact]
    public async Task ActiveAppDeliveryTypesWithoutTouchingClipboard()
    {
        var clipboard = new FakeClipboard("before");
        var windows = new FakeWindows { ExistsValue = true, FocusSucceeds = true };
        var keyboard = new FakeKeyboard(new TextInputResult(true, true), pasteResult: true);
        var service = new ActiveAppPasteService(clipboard, windows, keyboard, new ControlledDelay());

        var result = await service.PasteTextAsync("transcript", (IntPtr)42);

        Assert.True(result.TargetWasForeground);
        Assert.Equal("before", clipboard.Text);
        Assert.Equal(1, keyboard.TextCalls);
        Assert.Equal(0, keyboard.PasteCalls);
    }

    [Fact]
    public async Task ClipboardFallbackRestoresOnlyUnchangedMuesliClipboard()
    {
        var clipboard = new FakeClipboard("before");
        var windows = new FakeWindows { ExistsValue = true, FocusSucceeds = true };
        var delay = new ControlledDelay();
        var keyboard = new FakeKeyboard(new TextInputResult(false, false), pasteResult: true);
        var service = new ActiveAppPasteService(clipboard, windows, keyboard, delay);

        var result = await service.PasteTextAsync("transcript", (IntPtr)42);
        Assert.True(result.TargetWasForeground);
        Assert.Equal("transcript", clipboard.Text);
        delay.ReleaseRestore();
        await clipboard.Restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("before", clipboard.Text);
    }

    [Fact]
    public async Task ClipboardRaceDoesNotOverwriteNewUserContent()
    {
        var clipboard = new FakeClipboard("before");
        var delay = new ControlledDelay();
        var service = new ActiveAppPasteService(
            clipboard,
            new FakeWindows { ExistsValue = true, FocusSucceeds = true },
            new FakeKeyboard(new TextInputResult(false, false), pasteResult: true),
            delay);
        await service.PasteTextAsync("transcript", (IntPtr)42);
        clipboard.Text = "new user copy";
        delay.ReleaseRestore();
        await Task.Delay(50);
        Assert.Equal("new user copy", clipboard.Text);
        Assert.False(clipboard.Restored.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task PasteFailurePreservesExistingClipboard(bool exists, bool focus, bool injects)
    {
        var clipboard = new FakeClipboard("before");
        var service = new ActiveAppPasteService(
            clipboard,
            new FakeWindows { ExistsValue = exists, FocusSucceeds = focus },
            new FakeKeyboard(new TextInputResult(false, false), pasteResult: injects),
            new ControlledDelay());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PasteTextAsync("transcript", (IntPtr)42));
        Assert.Equal("before", clipboard.Text);
    }

    [Fact]
    public async Task PartialDirectInputDoesNotFallBackAndDoesNotReplaceClipboard()
    {
        var clipboard = new FakeClipboard("before");
        var keyboard = new FakeKeyboard(new TextInputResult(false, true), pasteResult: true);
        var service = new ActiveAppPasteService(
            clipboard,
            new FakeWindows { ExistsValue = true, FocusSucceeds = true },
            keyboard,
            new ControlledDelay());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PasteTextAsync("transcript", (IntPtr)42));

        Assert.Equal("before", clipboard.Text);
        Assert.Equal(0, keyboard.PasteCalls);
    }

    [Fact]
    public async Task ClipboardAdapterFailureAfterSettingTextRestoresExistingClipboard()
    {
        var clipboard = new FakeClipboard("before", throwAfterSet: true);
        var service = new ActiveAppPasteService(
            clipboard,
            new FakeWindows { ExistsValue = true, FocusSucceeds = true },
            new FakeKeyboard(new TextInputResult(false, false), pasteResult: true),
            new ControlledDelay());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PasteTextAsync("transcript", (IntPtr)42));

        Assert.Equal("before", clipboard.Text);
    }

    private sealed class FakeClipboard(string? initial, bool throwAfterSet = false) : IClipboardAdapter
    {
        public string? Text { get; set; } = initial;
        public bool ThrowAfterSet { get; } = throwAfterSet;
        public TaskCompletionSource Restored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClipboardSnapshot(new System.Windows.DataObject("Text", Text ?? "")));
        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Text = text;
            if (ThrowAfterSet)
            {
                throw new InvalidOperationException("simulated clipboard adapter failure");
            }
            return Task.CompletedTask;
        }
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => Task.FromResult(Text);
        public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
        {
            Text = (snapshot.Data as System.Windows.IDataObject)?.GetData("Text") as string;
            Restored.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWindows : IWindowActivationAdapter
    {
        public bool ExistsValue { get; init; }
        public bool FocusSucceeds { get; init; }
        public IntPtr ForegroundWindow { get; private set; }
        public bool Exists(IntPtr window) => ExistsValue;
        public bool RequestForeground(IntPtr window)
        {
            if (FocusSucceeds) ForegroundWindow = window;
            return FocusSucceeds;
        }
    }

    private sealed class FakeKeyboard(TextInputResult textResult, bool pasteResult) : IKeyboardInputAdapter
    {
        public int TextCalls { get; private set; }
        public int PasteCalls { get; private set; }

        public TextInputResult SendText(string text)
        {
            TextCalls++;
            return textResult;
        }

        public bool SendPasteShortcut()
        {
            PasteCalls++;
            return pasteResult;
        }
    }

    private sealed class ControlledDelay : IAsyncDelay
    {
        private readonly TaskCompletionSource _restore = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            delay >= TimeSpan.FromMilliseconds(400) ? _restore.Task : Task.CompletedTask;
        public void ReleaseRestore() => _restore.TrySetResult();
    }
}
