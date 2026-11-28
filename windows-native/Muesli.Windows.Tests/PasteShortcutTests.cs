using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>macOS "Paste shortcut", Windows choices: Ctrl+V, Ctrl+Shift+V, Shift+Insert, or typing.</summary>
public sealed class PasteShortcutTests
{
    private static readonly IntPtr Target = new(42);

    [Theory]
    [InlineData(PasteShortcuts.Automatic, PasteChord.CtrlV)]
    [InlineData(PasteShortcuts.CtrlShiftV, PasteChord.CtrlShiftV)]
    [InlineData(PasteShortcuts.ShiftInsert, PasteChord.ShiftInsert)]
    public async Task A_chord_pastes_through_the_clipboard_and_restores_it(string shortcut, PasteChord expected)
    {
        var (service, clipboard, keyboard) = Create();

        await service.PasteTextAsync("hello world ", Target, default, shortcut);

        Assert.Equal([expected], keyboard.Chords);
        Assert.Empty(keyboard.Typed);
        Assert.Equal("hello world ", clipboard.SetTexts.Single());
        Assert.True(clipboard.Restored, "the user's clipboard is put back after the paste");
    }

    [Fact]
    public async Task Type_the_text_types_and_never_touches_the_clipboard()
    {
        var (service, clipboard, keyboard) = Create();

        await service.PasteTextAsync("hello world ", Target, default, PasteShortcuts.TypeText);

        Assert.Equal(["hello world "], keyboard.Typed);
        Assert.Empty(keyboard.Chords);
        Assert.Empty(clipboard.SetTexts);
    }

    [Fact]
    public async Task A_refused_chord_still_falls_back_to_typing()
    {
        var (service, _, keyboard) = Create(acceptChord: false);

        await service.PasteTextAsync("hello", Target, default, PasteShortcuts.CtrlShiftV);

        Assert.Equal([PasteChord.CtrlShiftV], keyboard.Chords);
        Assert.Equal(["hello"], keyboard.Typed);
    }

    [Theory]
    [InlineData(null, PasteShortcuts.Automatic)]
    [InlineData("", PasteShortcuts.Automatic)]
    [InlineData("cmd_v", PasteShortcuts.Automatic)]
    [InlineData(" CTRL_SHIFT_V ", PasteShortcuts.CtrlShiftV)]
    [InlineData("type", PasteShortcuts.TypeText)]
    public void Unknown_values_read_as_automatic(string? value, string expected) =>
        Assert.Equal(expected, PasteShortcuts.Normalize(value));

    [Fact]
    public void Defaults_keep_todays_ctrl_v_and_the_settings_page_saves_the_choice()
    {
        Assert.Equal(PasteShortcuts.Automatic, new MuesliSettings().PasteShortcut);
        Assert.Equal("Automatic (Ctrl+V)", PasteShortcuts.Label(PasteShortcuts.Automatic));
        var viewModel = File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "ViewModels", "SettingsPageViewModel.cs"));
        Assert.Contains("settings with { PasteShortcut = PasteShortcuts.All[PasteShortcutIndex] }", viewModel, StringComparison.Ordinal);
    }

    private static (ActiveAppPasteService Service, FakeClipboard Clipboard, FakeKeyboard Keyboard) Create(bool acceptChord = true)
    {
        var clipboard = new FakeClipboard();
        var keyboard = new FakeKeyboard(acceptChord);
        return (new ActiveAppPasteService(clipboard, new FakeWindows(), keyboard, new NoDelay()), clipboard, keyboard);
    }

    private sealed class FakeClipboard : IClipboardAdapter
    {
        private string? _current = "user's own clipboard";
        public List<string> SetTexts { get; } = [];
        public bool Restored { get; private set; }

        public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new ClipboardSnapshot(_current));

        public Task SetTextAsync(string text, CancellationToken cancellationToken)
        {
            SetTexts.Add(text);
            _current = text;
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => Task.FromResult(_current);

        public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
        {
            _current = snapshot.Data as string;
            Restored = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWindows : IWindowActivationAdapter
    {
        public IntPtr ForegroundWindow => Target;
        public bool Exists(IntPtr window) => window == Target;
        public bool RequestForeground(IntPtr window) => true;
    }

    private sealed class FakeKeyboard(bool acceptChord) : IKeyboardInputAdapter
    {
        public List<PasteChord> Chords { get; } = [];
        public List<string> Typed { get; } = [];

        public bool SendPasteShortcut(PasteChord chord)
        {
            Chords.Add(chord);
            return acceptChord;
        }

        public TextInputResult SendText(string text)
        {
            Typed.Add(text);
            return new TextInputResult(true, true);
        }
    }

    private sealed class NoDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
