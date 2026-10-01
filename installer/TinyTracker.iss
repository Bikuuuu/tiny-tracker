; Tiny Tracker's installer (spec §11). scripts\build-installer.ps1 publishes the app and its licenses into Dist, then compiles this.
#ifndef AppVersion
  #error Build with scripts\build-installer.ps1
#endif
#ifndef Dist
  #error Build with scripts\build-installer.ps1
#endif
#define AppId "TinyTracker"
#define InstallDir "{autopf}\Tiny Tracker"

[Setup]
AppId={#AppId}
AppName=Tiny Tracker
AppVersion={#AppVersion}
AppPublisher=Bikuuuu
AppPublisherURL=https://github.com/Bikuuuu/tiny-tracker
AppSupportURL=https://github.com/Bikuuuu/tiny-tracker/issues
AppUpdatesURL=https://github.com/Bikuuuu/tiny-tracker/releases
AppCopyright=Copyright (c) 2026 Bikuuuu
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Tiny Tracker Setup
UninstallDisplayName=Tiny Tracker
UninstallDisplayIcon={app}\TinyTracker.exe
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22621
PrivilegesRequired=admin
DefaultDirName={#InstallDir}
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
LicenseFile={#Dist}\license.txt
CloseApplications=yes
RestartApplications=yes
SetupMutex=TinyTrackerSetup,Global\TinyTrackerSetup
WizardStyle=modern dynamic windows11
WizardImageFile=..\assets\installer\wizard-large-*.png
WizardSmallImageFile=..\assets\installer\wizard-small-*.png
SetupIconFile=..\assets\icon\generated\app.ico
Compression=lzma2/max
SolidCompression=yes
OutputDir={#Dist}
OutputBaseFilename=TinyTracker-Setup-{#AppVersion}-x64

[Messages]
WinVersionTooLowError=Tiny Tracker needs Windows 11, version 22H2 or later.
WindowsVersionNotSupported=Tiny Tracker needs Windows 11 on a PC with an Intel or AMD processor.

[CustomMessages]
StartWithWindows=Start Tiny Tracker with Windows
RemoveData=Also remove your settings and history?
OnlyFolder=Tiny Tracker installs only into %1.

[Files]
Source: "{#Dist}\app\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs uninsrestartdelete

[Icons]
Name: "{autoprograms}\Tiny Tracker"; Filename: "{app}\TinyTracker.exe"; Comment: "Keeps the apps you choose up to date"

; A self-update's Setup, which the helper downloads there (spec §6.5).
[UninstallDelete]
Type: filesandordirs; Name: "{app}\update"

[Run]
Filename: "{app}\TinyTracker.exe"; Parameters: "--start-with-windows"; Description: "{cm:StartWithWindows}"; Flags: postinstall runasoriginaluser; Check: IsFreshInstall
Filename: "{app}\TinyTracker.exe"; Description: "{cm:LaunchProgram,Tiny Tracker}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
var
  FreshInstall, WasRunning, Installed: Boolean;
  ForegroundTicks, ForegroundHeld: Integer;

function GetForegroundWindow: HWND; external 'GetForegroundWindow@user32.dll stdcall';
function GetWindowThreadProcessId(hWnd: HWND; var lpdwProcessId: DWORD): DWORD; external 'GetWindowThreadProcessId@user32.dll stdcall';
function GetCurrentThreadId: DWORD; external 'GetCurrentThreadId@kernel32.dll stdcall';
function AttachThreadInput(idAttach, idAttachTo: DWORD; fAttach: BOOL): BOOL; external 'AttachThreadInput@user32.dll stdcall';
function SetForegroundWindow(hWnd: HWND): BOOL; external 'SetForegroundWindow@user32.dll stdcall';
function SetWindowPos(hWnd: HWND; hWndInsertAfter: NativeInt; X, Y, cx, cy: Integer; uFlags: UINT): BOOL; external 'SetWindowPos@user32.dll stdcall';
function IsWindowVisible(hWnd: HWND): BOOL; external 'IsWindowVisible@user32.dll stdcall';
function IsIconic(hWnd: HWND): BOOL; external 'IsIconic@user32.dll stdcall';
function SetTimer(hWnd: HWND; nIDEvent: UINT_PTR; uElapse: UINT; lpTimerFunc: NativeInt): UINT_PTR; external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd: HWND; uIDEvent: UINT_PTR): BOOL; external 'KillTimer@user32.dll stdcall';

function InitializeSetup: Boolean;
begin
  FreshInstall := not RegKeyExists(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1');
  WasRunning := FindWindowByClassName('TinyTracker.Tray') <> 0;
  Result := True;
end;

// Only after an install that finished, just after Restart Manager started the app again.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Installed := True;
end;

// Restart Manager starts the app again only after an install that finished, so after one that failed or rolled back, Setup starts it
// itself: Tiny Tracker keeps running either way (spec §6.5). After one that finished, it gives Restart Manager's copy 10 s to show, so
// two don't start at once. A self-update's Setup can't delete itself from the update folder, so it goes at the next restart.
procedure DeinitializeSetup;
var
  Update: String;
  Waited, ResultCode: Integer;
begin
  Update := AddBackslash(ExpandConstant('{#InstallDir}')) + 'update\';
  if CompareText(Copy(ExpandConstant('{srcexe}'), 1, Length(Update)), Update) = 0 then
    RestartReplace(ExpandConstant('{srcexe}'), '');
  if not WasRunning then
    Exit;
  Waited := 0;
  while Installed and (FindWindowByClassName('TinyTracker.Tray') = 0) and (Waited < 10000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  if FindWindowByClassName('TinyTracker.Tray') = 0 then
    if not ExecAsOriginalUser(ExpandConstant('{#InstallDir}\TinyTracker.exe'), '--startup', '', SW_SHOWNORMAL, ewNoWait, ResultCode) then
      Log(Format('Tiny Tracker didn''t start again: %d', [ResultCode]));
end;

// After Windows' prompt, the wizard opens behind the window used before, and taking the foreground alone can leave it under that
// window (spec §11). So for its first 3 s on screen it stays above the other windows, and takes the foreground until it holds it,
// through the front window's input, as the flyout does after a prompt.
procedure TakeForeground(Wnd: HWND; Msg: UINT; Event: UINT_PTR; Time: DWORD);
var
  Front: HWND;
  Process, Thread: DWORD;
  Attached: Boolean;
begin
  if not IsWindowVisible(WizardForm.Handle) then
    Exit;
  if IsIconic(WizardForm.Handle) then
    Exit;
  ForegroundTicks := ForegroundTicks + 1;
  // HWND_TOPMOST, and HWND_NOTOPMOST at the end, which keeps it above the others; SWP_NOSIZE, NOMOVE, NOACTIVATE.
  if ForegroundTicks = 1 then
    SetWindowPos(WizardForm.Handle, -1, 0, 0, 0, 0, $13);
  Front := GetForegroundWindow;
  if ForegroundTicks > 30 then
  begin
    SetWindowPos(WizardForm.Handle, -2, 0, 0, 0, 0, $13);
    KillTimer(0, Event);
    Log(Format('Wizard in front: %d', [Ord(Front = WizardForm.Handle)]));
    Exit;
  end;
  if Front = WizardForm.Handle then
  begin
    ForegroundHeld := ForegroundHeld + 1;
    Exit;
  end;
  // Once it has held the foreground, a switch away is the user's.
  if ForegroundHeld >= 2 then
    Exit;
  ForegroundHeld := 0;
  Thread := 0;
  if Front <> 0 then
    Thread := GetWindowThreadProcessId(Front, Process);
  // Its own message boxes keep the focus.
  if Thread = GetCurrentThreadId then
    Exit;
  Attached := False;
  if Thread <> 0 then
    Attached := AttachThreadInput(GetCurrentThreadId, Thread, True);
  SetForegroundWindow(WizardForm.Handle);
  if Attached then
    AttachThreadInput(GetCurrentThreadId, Thread, False);
  Log(Format('Foreground at %d ms: front window %d, attached %d, in front %d', [ForegroundTicks * 100, Ord(Front <> 0),
    Ord(Attached), Ord(GetForegroundWindow = WizardForm.Handle)]));
end;

procedure InitializeWizard;
begin
  if not WizardSilent then
    SetTimer(0, 0, 100, CreateCallback(@TakeForeground));
end;

// An upgrade keeps Start with Windows as the user left it.
function IsFreshInstall: Boolean;
begin
  Result := FreshInstall;
end;

// There's no Ready page, so the license page installs.
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpLicense then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
end;

// The elevated helper must stay where only administrators can write (spec §8), so /DIR can't move it.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{#InstallDir}')) <> 0 then
    Result := FmtMessage(CustomMessage('OnlyFolder'), [ExpandConstant('{#InstallDir}')]);
end;

// Before any file goes, the app closes itself and removes what it registered (spec §10).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Parameters: String;
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  Parameters := '--uninstall';
  if UninstallSilent or (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO) = IDYES) then
    Parameters := Parameters + ' --remove-data';
  // 0 and 2 say which cleanup ran; the install test looks for the shell's line.
  if not Exec(ExpandConstant('{app}\TinyTracker.exe'), Parameters, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('TinyTracker.exe %s didn''t start: %d', [Parameters, ResultCode]))
  else if ResultCode = 0 then
    Log('The cleanup ran through the Windows shell')
  else if ResultCode = 2 then
    Log('The cleanup ran here, with no Windows shell')
  else
    Log(Format('TinyTracker.exe %s ended with %d', [Parameters, ResultCode]));
end;
