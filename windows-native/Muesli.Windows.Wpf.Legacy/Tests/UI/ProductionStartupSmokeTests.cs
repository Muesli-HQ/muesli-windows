using System.Diagnostics;

namespace Muesli.Windows.UITests;

[Collection("UiAutomation")]
[Trait(UiAutomationEnvironment.TraitName, UiAutomationEnvironment.TraitValue)]
public sealed class ProductionStartupSmokeTests
{
    [UiAutomationFact]
    public void Launch_visits_core_pages_and_requires_accessible_names() => StaRunner.Run(() =>
    {
        using var session = MuesliUiSession.LaunchProduction();
        session.Run(() =>
        {
            foreach (var name in new[]
                     {
                         "Navigate to Dictations",
                         "Navigate to Meetings",
                         "Navigate to Models",
                         "Navigate to Settings",
                         "Navigate to About",
                         "Use light theme",
                         "Use dark theme",
                         "Minimize Muesli window",
                         "Close Muesli window",
                         "Search dictations and meetings"
                     })
            {
                session.RequireAccessibleName(name);
            }

            session.NavigateTo("meetings", "Navigate to Meetings", "Manage Templates");
            session.RequireAccessibleName("Import media", mustBeOnscreen: true);

            session.NavigateTo("models", "Navigate to Models", "Prepare all models");

            session.NavigateTo("settings", "Navigate to Settings", "General");

            session.NavigateTo("about", "Navigate to About", "Open feature tour");
            session.RequireAccessibleName("Open Muesli logs");
            session.RequireAccessibleName("Check for Muesli updates");

            session.NavigateTo("dashboard", "Navigate to Dictations", "Search dictations and meetings");

            session.Invoke("Use light theme");
            session.RequireAccessibleName("Use dark theme");
            session.Invoke("Use dark theme");
            session.RequireAccessibleName("Use light theme");

            session.TabUntilNamedFocus();

            Assert.Equal(
                new[] { "dashboard", "meetings", "models", "settings", "about" },
                session.VisitedPages.Distinct(StringComparer.Ordinal).ToArray());
        });
    });

    [UiAutomationFact]
    public void Second_process_exits_and_primary_window_stays_attached() => StaRunner.Run(() =>
    {
        using var session = MuesliUiSession.LaunchProduction();
        session.Run(() =>
        {
            session.RequireAccessibleName("Navigate to Dictations");
            using var second = session.LaunchSecondInstance();
            var exited = second.WaitForExit(15_000);
            if (!exited)
            {
                try
                {
                    second.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                Assert.Fail("Second Muesli.exe did not exit; single-instance activation did not complete.");
            }

            session.ReattachMainWindow();
            session.RequireAccessibleName("Navigate to Dictations");
            Assert.False(session.ProcessId == second.Id);
        });
    });
}
