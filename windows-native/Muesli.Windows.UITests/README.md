# L09 Windows UI Automation

This project launches the built production 'Muesli.exe' out of process and
drives the dashboard through Windows UI Automation. It is an explicit opt-in
gate because the application currently resolves '%APPDATA%' through the
Windows known-folder API. The harness parks the existing 'muesli' directory,
installs a deterministic seed profile, and restores it after the test process
exits.

Run only on a disposable or otherwise approved interactive desktop:

    $env:MUESLI_UI_AUTOMATION = '1'
    dotnet test windows-native\Muesli.Windows.UITests\Muesli.Windows.UITests.csproj -c Debug --no-build

The harness never terminates a pre-existing 'Muesli.exe'; it refuses to run
when the matching executable is already running. It terminates only the primary
and secondary processes that it started, and it can pass
'MUESLI_SQLITE_HISTORY_CUTOVER=1' to those child processes through
'MuesliUiSession.LaunchProduction(enableSqliteHistoryCutover: true)'.

Shell navigation currently relies on the production controls' stable
'AutomationProperties.Name' values, so no locked production files are changed
by L09. L23 still needs an integration hook: the meeting-detail view should
expose stable accessible names (and, where practical, explicit
'AutomationProperties.AutomationId' values) for edit, save, cancel,
retranscribe, accept, and reject before meeting-detail UI automation is added.
