#define MyAppName "Site Media Downloader"
#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif
#define MyAppExeName "SiteMediaDownloader.exe"

[Setup]
AppId={{D052FC66-89AD-4DD0-BCE9-A34E55138767}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Site Media Downloader
DefaultDirName={localappdata}\Programs\Site Media Downloader
DefaultGroupName=Site Media Downloader
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist\installer
OutputBaseFilename=Site-Media-Downloader-Setup-{#MyAppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
Source: "..\dist\SiteMediaDownloader\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Site Media Downloader"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Site Media Downloader"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir Site Media Downloader"; Flags: nowait postinstall skipifsilent
