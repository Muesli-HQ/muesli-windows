using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-08 (macOS fd1b68f0): Escape never ends a meeting started with its shortcut.</summary>
public sealed class MeetingHotkeyEscapeTests
{
    [Fact]
    public void AShortcutWithoutACancelPathNeverOwnsEscape()
    {
        // The meeting shortcut registers no cancel predicate (App.RegisterMeetingShortcut).
        Assert.False(GlobalHotkeyService.OwnsEscape(escapeDown: false, canCancel: null));
    }

    [Fact]
    public void DictationKeepsItsCancelOnlyWhileItCanCancel()
    {
        Assert.True(GlobalHotkeyService.OwnsEscape(escapeDown: false, canCancel: () => true));
        Assert.False(GlobalHotkeyService.OwnsEscape(escapeDown: false, canCancel: () => false));
        // The release of a consumed press is consumed too, even if the session ended meanwhile.
        Assert.True(GlobalHotkeyService.OwnsEscape(escapeDown: true, canCancel: () => false));
    }
}
