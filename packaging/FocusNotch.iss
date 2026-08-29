; Focus Notch installer.
;
; Per-user by design. It installs under the user's own AppData, asks for no elevation and needs
; no administrator, which means anybody can install it on a managed machine and uninstalling it
; leaves nothing behind in Program Files or in the machine-wide registry.
;
; Built by package.ps1, which passes the version in rather than duplicating it here. Building
; this file directly without that define will fail on purpose.

#ifndef AppVersion
  #error Build this through package.ps1, which supplies AppVersion.
#endif

#define AppName "Focus Notch"
#define AppPublisher "Maks Winz"
#define AppUrl "https://github.com/makswinz/FocusNotch"
#define AppExe "FocusNotch.exe"

[Setup]
; Never change this GUID. It is what lets an upgrade replace the previous install rather than
; sitting beside it as a second entry in Add/Remove Programs.
AppId={{5131E6BF-7ACD-4E7B-8DCC-D84CA8CF5063}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Everything about this install is the user's own.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Programs\FocusNotch
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; Windows 10 1809 and later, 64-bit. The published build is win-x64 and self-contained.
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\artifacts
OutputBaseFilename=FocusNotch-Setup-{#AppVersion}
SetupIconFile=..\Assets\FocusNotch.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; The application keeps a database in AppData and it is not the installer's to delete. An
; uninstall removes the program; the tasks and the history stay until the user removes them.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start Focus Notch when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\artifacts\FocusNotch-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The application manages this key itself from its own settings panel; the installer only writes
; it when the box is ticked, and removes it on uninstall so nothing is left pointing at a file
; that is no longer there.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "FocusNotch"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; \
    Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the running instance before removing its files, so an uninstall never leaves a locked
; folder behind and never leaves a tray icon pointing at nothing.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopFocusNotch"
