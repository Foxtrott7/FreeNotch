; FreeNotch Per-User Inno Setup Script (no admin rights required)
; Build via: iscc /DMyAppVersion=1.0.2 /DMySourceDir=..\publish\win-x64-portable /DMyOutputDir=..\publish installer\FreeNotch.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif

#ifndef MySourceDir
  #define MySourceDir "..\publish\win-x64-portable"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\publish"
#endif

#define MyAppName "FreeNotch"
#define MyAppPublisher "FreeNotch"
#define MyAppURL "https://github.com/Foxtrott7/FreeNotch"
#define MyAppExeName "FreeNotch.exe"

[Setup]
AppId={{7B6F1C9D-4E2A-4D3F-9A8B-5C1E2F3A4B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#MyOutputDir}
OutputBaseFilename=FreeNotch-v{#MyAppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
SetupIconFile=..\Resources\FreeNotch.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.RemoveUserDataPrompt=Do you also want to delete all local FreeNotch settings and data (%AppData%\WindowsNotch)?%n%nChoose "No" to keep your settings for a future reinstallation.
german.RemoveUserDataPrompt=Möchten Sie auch alle lokalen FreeNotch-Einstellungen und -Daten (%AppData%\WindowsNotch) löschen?%n%nWählen Sie „Nein“, um Ihre Einstellungen für eine spätere Neuinstallation zu behalten.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunRegPath = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunRegValueName = 'WindowsNotch';
  LegacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\FreeNotch';

procedure StopRunningFreeNotch();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM FreeNotch.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM WindowsNotch.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningFreeNotch();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExistingRun: String;
  TargetExe: String;
begin
  if CurStep = ssPostInstall then
  begin
    // Keep existing user autostart pointed at the installed executable
    if RegQueryStringValue(HKCU, RunRegPath, RunRegValueName, ExistingRun) then
    begin
      TargetExe := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '"';
      RegWriteStringValue(HKCU, RunRegPath, RunRegValueName, TargetExe);
    end;

    // Remove legacy PowerShell per-user uninstall entry to avoid duplicates in Apps & Features
    if RegKeyExists(HKCU, LegacyUninstallKey) then
      RegDeleteKeyIncludingSubkeys(HKCU, LegacyUninstallKey);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningFreeNotch();
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegValueExists(HKCU, RunRegPath, RunRegValueName) then
      RegDeleteValue(HKCU, RunRegPath, RunRegValueName);

    DataDir := ExpandConstant('{userappdata}\WindowsNotch');
    if (not UninstallSilent) and DirExists(DataDir) then
    begin
      if MsgBox(CustomMessage('RemoveUserDataPrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
