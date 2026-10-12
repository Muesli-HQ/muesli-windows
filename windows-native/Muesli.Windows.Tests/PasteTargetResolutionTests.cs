using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class PasteTargetResolutionTests
{
    private sealed class FakeWindows : IWindowActivationAdapter, IWindowDescriptionAdapter
    {
        public IntPtr ForegroundWindow { get; set; }
        public Dictionary<IntPtr, PasteTargetInfo> Windows { get; } = [];
        public bool Exists(IntPtr window) => Windows.ContainsKey(window);
        public bool RequestForeground(IntPtr window) => true;
        public PasteTargetInfo DescribeWindow(IntPtr window) => Windows.GetValueOrDefault(window, PasteTargetInfo.Unknown);
    }

    private sealed class NoInput : IClipboardAdapter, IKeyboardInputAdapter
    {
        public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new ClipboardSnapshot(null));
        public Task SetTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool SendPasteShortcut() => true;
        public TextInputResult SendText(string text) => new(true, false);
    }

    [Fact]
    public void PastesWhereTheUserClickedButNeverIntoMuesli()
    {
        var windows = new FakeWindows();
        var input = new NoInput();
        var service = new ActiveAppPasteService(input, windows, input);
        IntPtr claude = 1, notepad = 2, pill = 3, closed = 4;
        windows.Windows[claude] = new("claude", 100);
        windows.Windows[notepad] = new("notepad", 200);
        windows.Windows[pill] = new("Muesli.Windows.Indicator.Wpf", 300);

        windows.ForegroundWindow = notepad;
        Assert.Equal(notepad, service.ResolvePasteTarget(claude));
        windows.ForegroundWindow = pill;
        Assert.Equal(claude, service.ResolvePasteTarget(claude));
        windows.ForegroundWindow = closed;
        Assert.Equal(claude, service.ResolvePasteTarget(claude));
        windows.ForegroundWindow = IntPtr.Zero;
        Assert.Equal(claude, service.ResolvePasteTarget(claude));
    }
}
