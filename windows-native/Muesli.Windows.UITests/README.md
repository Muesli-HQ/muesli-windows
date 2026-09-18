# L09 Windows UI Automation

This project launches the built production 'Muesli.exe' out of process and
drives the dashboard through Windows UI Automation. It is an explicit opt-in
gate for an interactive desktop. Each run creates a deterministic seed profile
under the OS temporary directory and passes it to the production shell through
'MUESLI_PROFILE_ROOT', so the developer's '%APPDATA%\\muesli' history is never
parked, modified, or used by the test process.

Run only on a disposable or otherwise approved interactive desktop:

    $env:MUESLI_UI_AUTOMATION = '1'
    dotnet test windows-native\Muesli.Windows.UITests\Muesli.Windows.UITests.csproj -c Debug --no-build

The harness never terminates a pre-existing 'Muesli.exe'; it refuses to run
when the matching executable is already running. It terminates only the primary
and secondary processes that it started, and it can pass
'MUESLI_SQLITE_HISTORY_CUTOVER=1' to those child processes through
'MuesliUiSession.LaunchProduction(enableSqliteHistoryCutover: true)'.

Shell navigation relies on the production controls' stable
'AutomationProperties.Name' values, and the meeting-detail controls now expose
stable accessible names (with explicit AutomationIds where practical) for edit,
save, cancel, retranscribe, accept, and reject. `MeetingDetailProductionTests`
covers the edit/save-cancel, retranscription candidate reject/accept,
navigation, and restart flows. The full Windows UI suite currently passes 6/6
with 0 skipped and 0 failed; live OS detection, paste, microphone,
accessibility, and physical qualification remain outside this harness.

The WinUI qualification flow uses `MuesliWinUiSession.LaunchPopulated()` for a
truthful local library fixture (six dictations, three meetings, three folders,
four dictionary entries, three suggestions, and three meeting templates). It
captures the eleven reference states plus meeting templates, search, and
meeting-detail surfaces with stable `winui-populated-*`/`winui-secondary-*`
slugs. Requested viewport sizes are effective DIPs (1280×820, 1440×900, and
1600×900); the session converts them to physical pixels using
`GetDpiForWindow`. The capability manifest records the observed scale and
explicitly reports High Contrast as unexercised. Generate a combined reference
contact sheet with
`scripts/qualify-winui-visual-contact-sheet.ps1`; output is written to
`artifacts/ui-parity-final/` while captures remain under the existing
`artifacts/ui-automation/` policy.
