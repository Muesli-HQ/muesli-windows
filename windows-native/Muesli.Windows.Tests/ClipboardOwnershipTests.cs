using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-16 (macOS f5d21107, 784e1a47): one clipboard owner across each paste transaction.</summary>
public sealed class ClipboardOwnershipTests
{
    /// <summary>A clipboard whose generation advances on every write, like GetClipboardSequenceNumber.</summary>
    private sealed class FakeClipboard : IClipboardAdapter, IKeyboardInputAdapter, IWindowActivationAdapter
    {
        public string? Text = "user text";
        public uint Generation { get; private set; } = 1;
        public bool ShortcutAccepted = true;
        public int Shortcuts;
        public bool FailWrite;

        public void ExternalWrite(string text) { Text = text; Generation++; }
        public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new ClipboardSnapshot(Text));
        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            Generation++;
            if (FailWrite) { Text = null; throw new InvalidOperationException("write failed"); }
            Text = text;
            return Task.CompletedTask;
        }
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => Task.FromResult(Text);
        public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
        {
            Text = (string?)snapshot.Data;
            Generation++;
            return Task.CompletedTask;
        }
        public bool SendPasteShortcut() { Shortcuts++; return ShortcutAccepted; }
        public TextInputResult SendText(string text) => new(false, false); // force the clipboard path
        public IntPtr ForegroundWindow => 1;
        public bool Exists(IntPtr window) => true;
        public bool RequestForeground(IntPtr window) => true;
    }

    /// <summary>Another app writes the clipboard right after Muesli's write is acknowledged.</summary>
    private sealed class RacingClipboard(FakeClipboard inner) : IClipboardAdapter
    {
        private int _reads;
        public uint Generation
        {
            get
            {
                var generation = inner.Generation;
                // Read 1: before the write. Read 2: ownership taken. Then another app writes.
                if (++_reads == 2) inner.ExternalWrite("other app");
                return generation;
            }
        }
        public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken) => inner.CaptureAsync(cancellationToken);
        public Task SetTextAsync(string text, CancellationToken cancellationToken) => inner.SetTextAsync(text, cancellationToken);
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => inner.ReadTextAsync(cancellationToken);
        public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken) => inner.RestoreAsync(snapshot, cancellationToken);
    }

    /// <summary>Delayed restores wait until the test releases them, so transactions can overlap.</summary>
    private sealed class HeldDelay : IAsyncDelay
    {
        public readonly List<TaskCompletionSource> Pending = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay < TimeSpan.FromMilliseconds(100)) return Task.CompletedTask;
            var held = new TaskCompletionSource(TaskCreationOptions.None);
            Pending.Add(held);
            return held.Task;
        }
    }

    private static ActiveAppPasteService Service(FakeClipboard clipboard, IAsyncDelay delay, IClipboardAdapter? adapter = null) =>
        new(adapter ?? clipboard, clipboard, clipboard, delay);

    [Fact]
    public async Task APasteRestoresTheUsersClipboardAfterwards()
    {
        var clipboard = new FakeClipboard();
        var delay = new HeldDelay();
        await Service(clipboard, delay).PasteTextAsync("dictated", 1);
        Assert.Equal("dictated", clipboard.Text);
        delay.Pending[0].SetResult();
        Assert.Equal("user text", clipboard.Text);
    }

    [Fact]
    public async Task OverlappingPastesInheritTheOriginalAndOnlyTheNewestRestores()
    {
        var clipboard = new FakeClipboard();
        var delay = new HeldDelay();
        var service = Service(clipboard, delay);
        await service.PasteTextAsync("first", 1);
        await service.PasteTextAsync("second", 1);
        delay.Pending[0].SetResult(); // the older delayed restore is superseded
        Assert.Equal("second", clipboard.Text);
        delay.Pending[1].SetResult();
        Assert.Equal("user text", clipboard.Text); // never Muesli's own "first"
    }

    [Fact]
    public async Task AnotherAppsLaterWriteIsNeverRestoredOver()
    {
        var clipboard = new FakeClipboard();
        var delay = new HeldDelay();
        var service = Service(clipboard, delay);
        await service.PasteTextAsync("dictated", 1);
        clipboard.ExternalWrite("dictated"); // even identical text from another owner
        delay.Pending[0].SetResult();
        Assert.Equal("dictated", clipboard.Text);
        Assert.Equal(1, clipboard.Shortcuts);
    }

    [Fact]
    public async Task OwnershipLossIsReportedSeparatelyFromAFailedShortcut()
    {
        var lost = new FakeClipboard();
        await Assert.ThrowsAsync<ClipboardOwnershipLostException>(() =>
            Service(lost, new HeldDelay(), new RacingClipboard(lost)).PasteTextAsync("dictated", 1));
        Assert.Equal(0, lost.Shortcuts); // nothing pasted
        Assert.Equal("other app", lost.Text); // and nothing restored over the new owner

        var refused = new FakeClipboard { ShortcutAccepted = false };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(refused, new HeldDelay()).PasteTextAsync("dictated", 1));
        Assert.IsNotType<ClipboardOwnershipLostException>(error);
        Assert.Equal("user text", refused.Text); // still owned, so restored
    }

    [Fact]
    public async Task AFailedWriteRestoresTheOriginal()
    {
        var clipboard = new FakeClipboard { FailWrite = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(clipboard, new HeldDelay()).PasteTextAsync("dictated", 1));
        Assert.Equal(0, clipboard.Shortcuts);
        Assert.Equal("user text", clipboard.Text);
    }
}
