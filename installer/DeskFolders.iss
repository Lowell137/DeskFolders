; DeskFolders installer (Inno Setup 6). Per-user install, no admin rights needed.
#define AppName "DeskFolders"
#define AppVersion "1.1.0"
#define AppExe "GrupPopup.exe"

[Setup]
AppId={{7C1B5E8A-3F2D-4B6A-9E1C-5D2F8A4B9C31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Lowell137
AppPublisherURL=https://github.com/Lowell137/DeskFolders
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\out
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=no

[Tasks]
Name: "startup"; Description: "Start with Windows"; GroupDescription: "Options:"

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Start with Windows (optional)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue
; Desktop right-click > New > App folder (Explorer creates an empty .grupfolder placeholder; the app turns it into a folder)
Root: HKCU; Subkey: "Software\Classes\.grupfolder"; ValueType: string; ValueData: "GrupPopup.AppFolder"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.grupfolder\ShellNew"; ValueType: string; ValueName: "NullFile"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\GrupPopup.AppFolder"; ValueType: string; ValueData: "App folder"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\GrupPopup.AppFolder"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "App folder"
Root: HKCU; Subkey: "Software\Classes\GrupPopup.AppFolder\DefaultIcon"; ValueType: string; ValueData: "{sys}\shell32.dll,4"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall

[Code]
procedure StopApp();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopApp();
  Result := True;
end;
