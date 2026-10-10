namespace Muesli.Windows.Tests;

/// <summary>
/// Source contracts for the 2026-10-10 launch-review P0s. The WinUI contexts are not compiled into
/// this assembly, so these pin the wiring the same way SoundFeedbackTests pins its toggle.
/// </summary>
public sealed class LaunchBlockerRegressionTests
{
    private static string Read(params string[] path) =>
        File.ReadAllText(TestRepositoryLayout.Combine(["windows-native", .. path]));

    [Fact]
    public void A_finished_dictation_releases_its_token_so_Escape_reaches_other_apps()
    {
        var dictation = Read("Muesli.Windows.WinUI", "Services", "WinUiDictationContext.cs");
        var stop = dictation[dictation.IndexOf("private async Task StopAsync()", StringComparison.Ordinal)..];
        var stopFinally = stop[stop.IndexOf("finally", StringComparison.Ordinal)..stop.IndexOf("private void StartTimers", StringComparison.Ordinal)];

        // The Escape hook treats any live operation token as an active dictation.
        Assert.Contains("_operationCancellation = null;", stopFinally, StringComparison.Ordinal);
    }

    [Fact]
    public void The_meeting_pill_never_discards_a_meeting_without_confirmation()
    {
        var host = Read("Muesli.Windows.WinUI", "Services", "WinUiIndicatorHost.cs");

        Assert.DoesNotContain("_meetings.CancelAsync(", host, StringComparison.Ordinal);
        Assert.Contains("_meetings.ConfirmAndDiscardAsync(", host, StringComparison.Ordinal);
    }

    [Fact]
    public void The_microphone_is_not_prepared_before_onboarding_completes()
    {
        var dictation = Read("Muesli.Windows.WinUI", "Services", "WinUiDictationContext.cs");
        var coordinator = Read("Muesli.Windows.Platform", "Services", "DictationCoordinator.cs");

        Assert.Contains("if (settings.OnboardingCompleted) _coordinator.PrepareMicrophone(", dictation, StringComparison.Ordinal);
        Assert.Single(dictation.Split("_coordinator.PrepareMicrophone(").Skip(1));
        // After a dictation the coordinator only re-prepares if the shell opted in.
        Assert.DoesNotContain("PrepareMicrophone(_microphoneName)", coordinator, StringComparison.Ordinal);
    }
}
