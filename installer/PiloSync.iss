; PiloSync per-user installer (Inno Setup 6).
; Build: ISCC /DTag=v2026.1117b /DVersion=1117b /DNumericVersion=2026.1117.0.0 /DPackageDir=<folder with the three files> /O<output folder> PiloSync.iss
; The installed layout must stay PiloSync.exe, pilosync-cli.exe and LICENSE directly in {app}:
; the tray updater (UpdateInstaller) swaps exactly these files and keeps the old ones in {app}\previous.

#ifndef Tag
  #error Define Tag, for example /DTag=v2026.1117b
#endif
#ifndef Version
  #error Define Version, for example /DVersion=1117b
#endif
#ifndef NumericVersion
  #error Define NumericVersion, for example /DNumericVersion=2026.1117.0.0
#endif
#ifndef PackageDir
  #error Define PackageDir, the folder with PiloSync.exe, pilosync-cli.exe and LICENSE
#endif

[Setup]
AppId={{6F2B9C41-3E7A-4D58-9B1C-2A7E5D0F8C34}
AppName=PiloSync
AppVersion={#Version}
AppVerName=PiloSync {#Version}
AppPublisherURL=https://github.com/zeusu-sato/PiloSync/
AppSupportURL=https://github.com/zeusu-sato/PiloSync/
AppUpdatesURL=https://github.com/zeusu-sato/PiloSync/releases/
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#Version}
VersionInfoDescription=PiloSync Setup
VersionInfoProductName=PiloSync
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\PiloSync
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=no
DisableFinishedPage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=PiloSync-Setup-{#Tag}
SetupIconFile=..\src\PiloSync.Cli\PiloSync.ico
UninstallDisplayIcon={app}\PiloSync.exe
UninstallDisplayName=PiloSync
CreateUninstallRegKey=not IsTestMode
; The release check scans the setup image only; the packaged files are checked after a real install on the runner.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage

[Languages]
; The first entry is used when the Windows display language matches neither.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Messages]
english.ReadyLabel1=Setup is ready to install PiloSync.
english.FinishedLabel=PiloSync is installed. Use it from the icon in the notification area.
english.FinishedLabelNoIcons=PiloSync is installed. Use it from the icon in the notification area.
english.ConfirmUninstall=Remove PiloSync? Your backups and accounts are kept.
japanese.ReadyLabel1=PiloSync をインストールします。
japanese.FinishedLabel=PiloSync のインストールが完了しました。通知領域のアイコンから操作できます。
japanese.FinishedLabelNoIcons=PiloSync のインストールが完了しました。通知領域のアイコンから操作できます。
japanese.ConfirmUninstall=PiloSync を削除します。バックアップとアカウント情報は残ります。よろしいですか？

[Files]
Source: "{#PackageDir}\PiloSync.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\pilosync-cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{code:StartMenuDir}\PiloSync"; Filename: "{app}\PiloSync.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PiloSync.exe"; WorkingDir: "{app}"; Flags: nowait skipifsilent; Check: not IsTestMode

[UninstallDelete]
; Created by the tray updater. Data under %LOCALAPPDATA%\PiloSync and Core\Path is never listed here.
Type: filesandordirs; Name: "{app}\previous"
Type: files; Name: "{app}\installer-test.txt"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  TestRunKey = 'Software\PiloSync-InstallerTest\Run';
  TestMarker = 'installer-test.txt';

var
  TestRoot: String;

{ /PILOSYNCTEST=<folder> is for local checks only: it keeps the real Run value,
  Start menu, uninstall entry and running PiloSync untouched. }
function IsTestMode: Boolean;
begin
  Result := TestRoot <> '';
end;

function InitializeSetup: Boolean;
begin
  TestRoot := ExpandConstant('{param:PILOSYNCTEST|}');
  Result := True;
end;

function StartMenuDir(Param: String): String;
begin
  if IsTestMode then
    Result := AddBackslash(TestRoot) + 'StartMenu'
  else
    Result := ExpandConstant('{userprograms}');
end;

function RunCommand: String;
begin
  Result := '"' + ExpandConstant('{app}\PiloSync.exe') + '" tray';
end;

function PointsToApp(Value: String): Boolean;
var
  Text: String;
begin
  Text := Lowercase(Trim(Value));
  Result := (Pos(Lowercase('"' + ExpandConstant('{app}\PiloSync.exe') + '"'), Text) = 1) or
            (Pos(Lowercase('"' + ExpandConstant('{app}\pilosync-cli.exe') + '"'), Text) = 1) or
            (Pos(Lowercase(ExpandConstant('{app}\PiloSync.exe')), Text) = 1) or
            (Pos(Lowercase(ExpandConstant('{app}\pilosync-cli.exe')), Text) = 1);
end;

{ Stops the resident PiloSync of this user, wherever it was started from. }
procedure StopPiloSync;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM pilosync-cli.exe /T', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if not IsTestMode then StopPiloSync;
  Result := '';
end;

{ install-startup disables the stock HotSync auto-start and points Run\PiloSync at the installed PiloSync.exe.
  If it fails, only the Run value is written. }
procedure RegisterStartup;
var
  Code: Integer;
  Value: String;
begin
  if IsTestMode then
  begin
    SaveStringToFile(ExpandConstant('{app}\') + TestMarker, TestRoot, False);
    RegWriteStringValue(HKEY_CURRENT_USER, TestRunKey, 'PiloSync', RunCommand);
    Exit;
  end;
  if not Exec(ExpandConstant('{app}\pilosync-cli.exe'), 'install-startup', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    Log('install-startup could not start')
  else
    Log('install-startup exit code ' + IntToStr(Code));
  if not RegQueryStringValue(HKEY_CURRENT_USER, RunKey, 'PiloSync', Value) or (CompareText(Trim(Value), RunCommand) <> 0) then
    RegWriteStringValue(HKEY_CURRENT_USER, RunKey, 'PiloSync', RunCommand);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RegisterStartup;
end;

function InitializeUninstall: Boolean;
var
  Text: AnsiString;
begin
  TestRoot := '';
  if LoadStringFromFile(ExpandConstant('{app}\') + TestMarker, Text) then TestRoot := Trim(String(Text));
  Result := True;
end;

{ Removes only this installation's auto-start. Backups, accounts and Core\Path are not touched. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Value: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if IsTestMode then
  begin
    if RegQueryStringValue(HKEY_CURRENT_USER, TestRunKey, 'PiloSync', Value) and PointsToApp(Value) then
      RegDeleteValue(HKEY_CURRENT_USER, TestRunKey, 'PiloSync');
    Exit;
  end;
  StopPiloSync;
  if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, 'PiloSync', Value) and PointsToApp(Value) then
  begin
    { uninstall-startup also restores the stock HotSync auto-start it disabled. }
    Exec(ExpandConstant('{app}\pilosync-cli.exe'), 'uninstall-startup', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code);
    if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, 'PiloSync', Value) and PointsToApp(Value) then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'PiloSync');
  end;
end;
