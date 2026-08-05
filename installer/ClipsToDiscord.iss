#ifndef MyAppVersion
  #error MyAppVersion must be supplied by scripts/build-installer.ps1
#endif

#ifndef PackageDir
  #error PackageDir must be supplied by scripts/build-installer.ps1
#endif

#ifndef OutputDir
  #error OutputDir must be supplied by scripts/build-installer.ps1
#endif

#ifndef RepositoryRoot
  #error RepositoryRoot must be supplied by scripts/build-installer.ps1
#endif

#define MyAppName "ClipCord"
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
OutputBaseFilename=ClipCord-Setup
SetupIconFile={#RepositoryRoot}\assets\ClipsToDiscord.ico
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
Name: "{userprograms}\ClipCord"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{userdesktop}\ClipCord"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
; Remove shortcuts created by pre-ClipCord versions while retaining the stable
; AppId, install directory, executable name, data directory, and mutex for upgrades.
Type: files; Name: "{userprograms}\Clips to Discord.lnk"
Type: files; Name: "{userdesktop}\Clips to Discord.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ClipsToDiscord"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Flags: nowait; Check: IsInAppUpdate

[Code]
function IsInAppUpdate: Boolean;
begin
  Result := CompareText(ExpandConstant('{param:clipcordrestart|0}'), '1') = 0;
end;
