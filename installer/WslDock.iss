#ifndef MyAppVersion
  #error MyAppVersion must be passed by scripts/package.ps1
#endif

[Setup]
AppId={{E8B97873-6A71-437A-B58B-03B635DD4266}
AppName=WslDock
AppVersion={#MyAppVersion}
AppPublisher=XhosaS
AppPublisherURL=https://github.com/XhosaS/WslDock
AppSupportURL=https://github.com/XhosaS/WslDock/issues
DefaultDirName={localappdata}\Programs\WslDock
DefaultGroupName=WslDock
DisableProgramGroupPage=yes
OutputDir=..\artifacts\release
OutputBaseFilename=WslDock-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
PrivilegesRequired=lowest
SetupIconFile=..\src\WslDock\Assets\WslDock.ico
UninstallDisplayIcon={app}\WslDock.exe
CloseApplications=yes
RestartApplications=no
AppMutex=Local\WslDock.Desktop

[Files]
Source: "..\artifacts\WslDock\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\WslDock"; Filename: "{app}\WslDock.exe"
Name: "{autodesktop}\WslDock"; Filename: "{app}\WslDock.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WslDock.exe"; Description: "Launch WslDock"; Flags: nowait postinstall skipifsilent

[Code]
procedure RemoveOwnStartup();
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WslDock', Command) then
    if CompareText(Command, '"' + ExpandConstant('{app}\WslDock.exe') + '" --startup') = 0 then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'WslDock');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveOwnStartup();
end;
