; Inno Setup script (https://jrsoftware.org/isinfo.php). Build after publish.cmd. Per-user install: no admin needed.
; Refuse to build an installer without the bundle (publish.cmd has already checked that PHP, Apache, nginx and MariaDB are all in it).
#if !FileExists(SourcePath + "..\dist\bundle\manifest.json")
  #error "dist\bundle is missing. Run bundle.cmd, then publish.cmd (without --no-bundle), then build this installer."
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; AppId must never change once released: Windows uses it to find the existing install for upgrades and the uninstaller.
AppId={{6547A827-EA24-4B0C-95A7-099C2427D6AF}
AppName=Rhapsody Server
AppVersion={#AppVersion}
AppPublisher=Rhapsody Server contributors
AppPublisherURL=https://rhapsodyframework.com/software/rhapsody-server
AppSupportURL=https://github.com/arout77/Rhapsody-Server/issues
AppUpdatesURL=https://github.com/arout77/Rhapsody-Server/releases
UninstallDisplayName=Rhapsody Server
CloseApplications=yes
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
; Licence notices for the bundled third-party programs (MariaDB and phpMyAdmin are GPL: these point to their source).
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}\licenses"; DestName: "RhapsodyServer-LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Rhapsody Server"; Filename: "{app}\RhapsodyServer.exe"
Name: "{userstartup}\Rhapsody Server"; Filename: "{app}\RhapsodyServer.exe"; Parameters: "--no-splash"; Tasks: autostart

[Run]
Filename: "{app}\RhapsodyServer.exe"; Parameters: "--open"; Description: "Start Rhapsody Server and open the dashboard"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close the tray app so its files can be removed. (No /T: its child services are not killed abruptly.)
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM RhapsodyServer.exe"; Flags: runhidden; RunOnceId: "StopRhapsodyServer"

[Code]
// Uninstall removes the program only. Settings, downloaded components and databases live in
// %LOCALAPPDATA%\RhapsodyServer; ask before deleting them (default answer: keep). Site folders are never touched.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\RhapsodyServer');
    if DirExists(DataDir) then
      if MsgBox('Also delete Rhapsody Server''s settings, downloaded components and databases?'#13#10#13#10 + DataDir + #13#10#13#10'Your site folders are not touched.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
