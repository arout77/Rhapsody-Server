; Inno Setup script (https://jrsoftware.org/isinfo.php). Build after publish.cmd. Per-user install: no admin needed.
; Refuse to build an installer without the bundle (publish.cmd has already checked that PHP, Apache, nginx and MariaDB are all in it).
#if !FileExists(SourcePath + "..\dist\bundle\manifest.json")
  #error "dist\bundle is missing. Run bundle.cmd, then publish.cmd (without --no-bundle), then build this installer."
#endif

#ifndef AppVersion
  #define AppVersion "0.2"
#endif

[Setup]
AppName=Rhapsody Server
AppVersion={#AppVersion}
AppPublisher=Rhapsody Server contributors
SetupIconFile=rhapsody-server.ico
UninstallDisplayIcon={app}\RhapsodyServer.exe
DefaultDirName={localappdata}\Programs\Rhapsody Server
DefaultGroupName=Rhapsody Server
PrivilegesRequired=lowest
OutputDir=..\installer-out
OutputBaseFilename=RhapsodyServer-Setup
Compression=lzma2/fast
SolidCompression=no
; the bundled zips are already compressed, so there is little to gain from heavier settings

[Tasks]
Name: "autostart"; Description: "Start Rhapsody Server when I sign in (keeps the dashboard address always available)"

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Rhapsody Server"; Filename: "{app}\RhapsodyServer.exe"
Name: "{userstartup}\Rhapsody Server"; Filename: "{app}\RhapsodyServer.exe"; Parameters: "--no-splash"; Tasks: autostart

[Run]
Filename: "{app}\RhapsodyServer.exe"; Parameters: "--open"; Description: "Start Rhapsody Server and open the dashboard"; Flags: nowait postinstall skipifsilent
