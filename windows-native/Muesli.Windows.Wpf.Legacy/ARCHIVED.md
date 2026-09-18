# Archived WPF shell

This directory contains the retired WPF application shell. It is preserved for historical reference and recovery only.

The shipping Windows application is `../Muesli.Windows.WinUI/Muesli.Windows.WinUI.csproj`. The WPF project is intentionally excluded from `../Muesli.Windows.sln` and from the default build, test, launch, and package paths.

WPF-only unit and UI automation sources are retained under `Tests/`. Historical WPF-only release and visual-verification scripts are retained under `../../scripts/archive/wpf/`.

Do not add new product work here. Shared nonvisual behavior belongs in `Muesli.Windows.Core` or `Muesli.Windows.Platform`; active UI work belongs in `Muesli.Windows.WinUI`.
