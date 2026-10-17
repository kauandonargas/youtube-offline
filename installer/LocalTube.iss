#define MyAppName "YouTube Offline"
#ifndef MyAppVersion
  #define MyAppVersion "0.5.3"
#endif
#define MyAppPublisher "YouTube Offline"
#define MyAppExeName "YouTubeOffline.exe"

[Setup]
AppId={{58B447D3-FE93-4C5A-8E32-DB48AA6B8539}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\YouTube Offline
DefaultGroupName=YouTube Offline
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist\installer
OutputBaseFilename=YouTube-Offline-Setup-{#MyAppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\LocalTube.App\Assets\YouTubeOffline.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na area de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
Source: "..\dist\YouTubeOffline\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\YouTube Offline"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\YouTube Offline"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{app}\LocalTube.exe"
Type: files; Name: "{group}\LocalTube.lnk"
Type: files; Name: "{autodesktop}\LocalTube.lnk"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir YouTube Offline"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent
