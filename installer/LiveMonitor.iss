; LiveMonitor — Inno Setup installer script
; Requires Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
; Build: compile this .iss after running:
;   dotnet publish SqlHealthAssessment.sln -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/release

#define AppName      "LiveMonitor"
#define AppPublisher "Adrian Sullivan"
#define AppURL       "https://sqladrian.github.io/SqlHealthAssessment/"
#define AppExeName   "SqlHealthAssessment.exe"
#define AppVersion   "0.85.2"
#define BuildNumber  "1130"
#define SourceDir    "..\publish\release"
#define OutputDir    "..\release"

[Setup]
AppId={{A7F3C2D1-4E8B-4F9A-B3C7-1D2E5F6A8B9C}
AppName={#AppName}
AppVersion={#AppVersion}.{#BuildNumber}
AppVerName={#AppName} v{#AppVersion} build {#BuildNumber}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL=https://github.com/SQLAdrian/SqlHealthAssessment/issues
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
; No UAC prompt for per-user install — DBAs often lack admin on their workstation
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=LiveMonitor-v{#AppVersion}-build{#BuildNumber}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=assets\wizard-small.bmp
; WizardImageFile=assets\wizard-banner.bmp
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}.{#BuildNumber}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Free SQL Server monitoring for Windows DBAs
VersionInfoCopyright=Copyright (C) 2024-2026 {#AppPublisher}
MinVersion=10.0.17763
; Windows 10 1809 minimum (17763)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full";    Description: "Full installation"
Name: "compact"; Description: "Compact installation (no optional components)"
Name: "custom";  Description: "Custom installation"; Flags: iscustom

[Components]
Name: "main";        Description: "LiveMonitor core application"; Types: full compact custom; Flags: fixed
Name: "desktopicon"; Description: "Desktop shortcut";            Types: full
Name: "autostart";   Description: "Start with Windows (current user)"; Types: full

[Tasks]
Name: "desktopicon";  Description: "Create a &desktop shortcut";         GroupDescription: "Additional shortcuts:"; Components: desktopicon
Name: "quicklaunch";  Description: "Create a &Quick Launch shortcut";    GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Main application — single self-contained exe, no .NET required
Source: "{#SourceDir}\{#AppExeName}";         DestDir: "{app}";            Flags: ignoreversion
; Config files — preserve user edits on upgrade using onlyifdoesntexist
Source: "{#SourceDir}\config\appsettings.json";           DestDir: "{app}\config"; Flags: onlyifdoesntexist
Source: "{#SourceDir}\config\dashboard-config.json";      DestDir: "{app}\config"; Flags: onlyifdoesntexist
Source: "{#SourceDir}\config\alert-definitions.json";     DestDir: "{app}\config"; Flags: onlyifdoesntexist
Source: "{#SourceDir}\config\version.json";               DestDir: "{app}\config"; Flags: ignoreversion
; Deploy folder (SQLWATCH DACPACs and scripts)
Source: "{#SourceDir}\Deploy\*";              DestDir: "{app}\Deploy";     Flags: ignoreversion recursesubdirs createallsubdirs
; BPScripts
Source: "{#SourceDir}\BPScripts\*";           DestDir: "{app}\BPScripts";  Flags: ignoreversion recursesubdirs createallsubdirs; Components: main
; PlanViewer
Source: "{#SourceDir}\PlanViewer.Core.pdb";   DestDir: "{app}";            Flags: ignoreversion; Components: main

[Icons]
Name: "{group}\{#AppName}";                     Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} — Uninstall";         Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";               Filename: "{app}\{#AppExeName}";  Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: quicklaunch

[Registry]
; Autostart (current user only — no admin required)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Components: autostart

[Run]
; "Launch now" checkbox on the final page
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up log files and SQLite cache on uninstall (optional — user prompted)
Type: filesandordirs; Name: "{app}\logs"

[Code]
// ── Upgrade detection ─────────────────────────────────────────────────────
function GetUninstallString(): String;
var
  sUnInstPath: String;
  sUnInstallString: String;
begin
  sUnInstPath := ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1');
  sUnInstallString := '';
  if not RegQueryStringValue(HKLM, sUnInstPath, 'UninstallString', sUnInstallString) then
    RegQueryStringValue(HKCU, sUnInstPath, 'UninstallString', sUnInstallString);
  Result := sUnInstallString;
end;

function IsUpgrade(): Boolean;
begin
  Result := (GetUninstallString() <> '');
end;

function UnInstallOldVersion(): Integer;
var
  sUnInstallString: String;
  iResultCode: Integer;
begin
  Result := 0;
  sUnInstallString := GetUninstallString();
  if sUnInstallString <> '' then begin
    sUnInstallString := RemoveQuotes(sUnInstallString);
    if Exec(sUnInstallString, '/SILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, iResultCode) then
      Result := iResultCode
    else
      Result := 1;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    if IsUpgrade() then
      UnInstallOldVersion();
  end;
end;

// ── Welcome page messaging ────────────────────────────────────────────────
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
