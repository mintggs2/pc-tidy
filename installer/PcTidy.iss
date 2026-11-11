; PC Tidy: Inno Setup 6 script (per-machine, one admin prompt).
;
; Equivalent of our own installer (src\PcTidy.Setup), for when Inno Setup is available.
; build-release.ps1 runs it automatically if ISCC.exe is found:
;   ISCC.exe /DMyAppVersion=1.0.0 /DSourceDir=<payload folder: PcTidy.exe, PC Tidy Rescue.exe, native DLLs, docs> /O<output> installer\PcTidy.iss
; Same behaviour as PC Tidy Setup: installs to %ProgramFiles%\PC Tidy (admin-only, so the parts of PC Tidy that run
; with admin rights can't be tampered with by other programs), Start-menu entries for PC Tidy and PC Tidy Rescue in
; the all-users Start menu, optional desktop icon, a machine-wide uninstall entry in Settings > Apps, and an
; uninstaller that removes what PC Tidy added and asks before deleting the user's data (default: keep).
;
; Differences from PC Tidy Setup (which does the user's part with the user's normal rights):
;  - Inno runs the whole install and uninstall elevated. With a standard account that typed an admin's password,
;    {localappdata} and HKCU are the ADMIN's, so the user's own right-click verbs / start-with-Windows value / data
;    folder aren't cleaned up and PC Tidy isn't started for the user afterwards. Prefer PC Tidy Setup.
;  - No spare copy of Rescue is written into the user's data folder (an elevated write into a user folder is avoided).

#define MyAppName "PC Tidy"
#define MyAppPublisher "PC Tidy"
#define MyAppExe "PcTidy.exe"
#define MyRescueExe "PC Tidy Rescue.exe"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\.work\payload"
#endif

[Setup]
; Keep this AppId forever: it is how Inno recognises an earlier install (update in place).
AppId={{6F1C3C55-2B7E-4C8B-9A61-3E0D5C1A7B42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Per-machine install: one admin prompt; {autopf} = %ProgramFiles%, {autoprograms}/{autodesktop} = all users.
PrivilegesRequired=admin
DefaultDirName={autopf}\{#MyAppName}
; The folder is fixed: only Program Files is admin-only, and the app refuses admin steps from anywhere else.
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 22H2 (build 19045) or Windows 11
MinVersion=10.0.19045
WizardStyle=modern
SetupIconFile=..\src\PcTidy.App\Assets\PcTidy.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
; No licence page: the MIT License needs no click-through. LICENSE and THIRD-PARTY-NOTICES.txt ship with the app.
OutputBaseFilename=PC-Tidy-Setup-{#MyAppVersion}-InnoSetup
Compression=lzma2/max
SolidCompression=yes
; Ask running copies of PC Tidy to close (Restart Manager) before files are replaced or removed.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs [name/ver] in Program Files for everyone on this PC.%n%nWindows asks for admin permission once, so the parts of PC Tidy that run with admin rights can't be tampered with by other programs. PC Tidy itself runs with your normal rights.

[Tasks]
Name: "desktopicon"; Description: "Add a shortcut to the desktop"; Flags: unchecked

[Files]
; uninstall.exe is PC Tidy Setup's own uninstaller; Inno has its own.
Source: "{#SourceDir}\*"; Excludes: "payload.json,uninstall.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Comment: "Clean up, speed up and organize your PC, safely."
Name: "{autoprograms}\{#MyAppName}\PC Tidy Rescue"; Filename: "{app}\{#MyRescueExe}"; IconFilename: "{app}\{#MyAppExe}"; Comment: "Undo what PC Tidy changed, even if PC Tidy won't start."
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
; runasoriginaluser: PC Tidy must never start with the installer's admin rights.
Filename: "{app}\{#MyAppExe}"; Description: "Start PC Tidy now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  ShellVerbPrefix = 'PCTidy.';

{ Removes every right-click verb PC Tidy added under one HKCU\Software\Classes\...\shell key. }
procedure RemoveVerbs(const ShellKey: String);
var
  Names: TArrayOfString;
  I: Integer;
begin
  if RegGetSubkeyNames(HKCU, ShellKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(Lowercase(ShellVerbPrefix), Lowercase(Names[I])) = 1 then
        RegDeleteKeyIncludingSubkeys(HKCU, ShellKey + '\' + Names[I]);
end;

{ Scheduled tasks in "\PC Tidy\" or named "PC Tidy*". This PowerShell runs with admin rights, so it must not load
  anything from folders the user can change: no profile, PSModulePath limited to Windows' own modules, and the
  ScheduledTasks module imported by its full System32 path and its cmdlets called by module-qualified name. }
procedure RemoveTasks;
var
  Code: Integer;
  Module: String;
begin
  Module := ExpandConstant('{sys}\WindowsPowerShell\v1.0\Modules\ScheduledTasks\ScheduledTasks.psd1');
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$env:PSModulePath = ''' + ExpandConstant('{sys}\WindowsPowerShell\v1.0\Modules') + '''; ' +
    'Import-Module -Name ''' + Module + ''' -ErrorAction Stop; ' +
    'ScheduledTasks\Get-ScheduledTask -TaskPath ''\PC Tidy\'' -ErrorAction SilentlyContinue | ScheduledTasks\Unregister-ScheduledTask -Confirm:$false; ' +
    'ScheduledTasks\Get-ScheduledTask -TaskPath ''\'' -TaskName ''PC Tidy*'' -ErrorAction SilentlyContinue | ScheduledTasks\Unregister-ScheduledTask -Confirm:$false"',
    ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ The data folder: %LOCALAPPDATA%\PC Tidy, or the folder named in its datafolder.txt (moved in Settings). }
function DataFolder: String;
var
  Pointer: AnsiString;
begin
  Result := ExpandConstant('{localappdata}\PC Tidy');
  if LoadStringFromFile(Result + '\datafolder.txt', Pointer) then
  begin
    Pointer := Trim(Pointer);
    if (Length(Pointer) > 3) and DirExists(Pointer) and
       (DirExists(Pointer + '\runs') or FileExists(Pointer + '\settings.json') or DirExists(Pointer + '\Quarantine')) then
      Result := Pointer;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  RemoveVerbs('Software\Classes\*\shell');
  RemoveVerbs('Software\Classes\Directory\shell');
  RemoveVerbs('Software\Classes\Directory\Background\shell');
  RemoveVerbs('Software\Classes\Drive\shell');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PC Tidy');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'PC Tidy');
  RemoveTasks;

  { Default: keep the data (it holds the history and the quarantine). Silent uninstalls always keep it. }
  Data := DataFolder;
  if (not UninstallSilent) and DirExists(Data) then
    if MsgBox('Also delete your PC Tidy data?' + #13#10#13#10 +
              Data + ' holds your history and the quarantine (files PC Tidy set aside so you can get them back). ' +
              'If you delete it, they are gone for good.' + #13#10#13#10 +
              'Choose No to keep it (recommended).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(Data, True, True, True);
      if CompareText(Data, ExpandConstant('{localappdata}\PC Tidy')) <> 0 then
        DelTree(ExpandConstant('{localappdata}\PC Tidy'), True, True, True);
    end;
end;
