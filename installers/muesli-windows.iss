#define MyAppName "Muesli"
#define MyAppPublisher "Muesli contributors"
#define MyAppExeName "Muesli.exe"
#ifndef MyAppVersion
  #error MyAppVersion must be supplied by scripts\build-installer.ps1.
#endif
#ifndef PublishSource
  #error PublishSource must be supplied by scripts\build-installer.ps1.
#endif

[Setup]
AppId={{80F6E82C-52CB-43F7-9C66-12B8C7D3E5F7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Muesli
DefaultGroupName=Muesli
DisableProgramGroupPage=yes
OutputBaseFilename=MuesliSetup-{#MyAppVersion}-win-x64
OutputDir=..\artifacts
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "startatlogin"; Description: "Start Muesli when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Muesli"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Muesli"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Muesli"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Muesli"; ValueData: """{app}\{#MyAppExeName}"" --background"; Tasks: startatlogin
