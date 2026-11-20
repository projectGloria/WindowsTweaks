#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "Windows Tweaks"
#define MyAppPublisher "projectGloria"
#define MyAppURL "https://github.com/projectGloria/WindowsTweaks"
#define MyAppExeName "WindowsTweaks.exe"

[Setup]
AppId={{A7C9E4B2-3F1D-4E8A-9B6C-2D5F8A1E4C7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\WindowsTweaks
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\Windows Tweaks.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=WindowsTweaks-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\WindowsTweaks.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\*.reg"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\WinUtil-Advanced.bat"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec
