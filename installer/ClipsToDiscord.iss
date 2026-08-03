#ifndef MyAppVersion
  #error MyAppVersion must be supplied by scripts/build-installer.ps1
#endif

#ifndef PackageDir
  #error PackageDir must be supplied by scripts/build-installer.ps1
#endif

#ifndef OutputDir
  #error OutputDir must be supplied by scripts/build-installer.ps1
#endif

#define MyAppName "Clips to Discord"
#define MyAppExeName "ClipsToDiscord.exe"
#define MyAppPublisher "Malik Pervez"
#define MyAppUrl "https://github.com/malikpervez/clips-to-discord"

[Setup]
AppId={{D8DAD06E-39D5-4C5A-A716-FAE1C8F27066}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases/latest
DefaultDirName={localappdata}\Programs\ClipsToDiscord
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=ClipsToDiscord-Setup
SetupArchitecture=x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=Local\ClipsToDiscord_Application
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} per-user installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PackageDir}\ClipsToDiscord.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#PackageDir}\FFMPEG-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{userprograms}\Clips to Discord"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{userdesktop}\Clips to Discord"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ClipsToDiscord"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
