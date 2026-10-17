; Build with Build-Installer.ps1. Keep AppId stable across every future version.
#if VER < EncodeVer(6, 6, 0)
  #error Disturn requires Inno Setup 6.6 or later for native dark wizard styling.
#endif
#ifndef AppVersion
  #error Build with Build-Installer.ps1 to supply the project version.
#endif
#ifndef AppFileVersion
  #error Build with Build-Installer.ps1 to supply the numeric Windows file version.
#endif
#define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish\Disturn-win-x64"
#if !FileExists(PublishDir + "\Disturn.exe")
  #error Publish Disturn with the Unpackaged-win-x64 profile before compiling.
#endif

[Setup]
LicenseFile=..\LICENSE
AppId={{AD661449-F6C0-44D0-BD80-4199B142600A}
AppName=Disturn
AppVersion={#AppVersion}
AppVerName=Disturn {#AppVersion}
AppPublisher=RixZC
VersionInfoVersion={#AppFileVersion}
VersionInfoTextVersion={#AppFileVersion}
VersionInfoProductVersion={#AppFileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoProductName=Disturn
VersionInfoDescription=Disturn Setup
DefaultDirName={localappdata}\Programs\Disturn
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
UsePreviousAppDir=yes
UsePreviousTasks=yes
Uninstallable=yes
UninstallDisplayName=Disturn
UninstallDisplayIcon={app}\Assets\Disturn.ico
SetupIconFile=..\Disturn\Assets\Disturn.ico
OutputDir=..\artifacts\installer
OutputBaseFilename=Disturn-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dark
; Window Close only hides Disturn. Never ask Restart Manager to close/kill it.
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
; Preserve all WinUI/native DLLs, PRI/WinMD files, locales, and resources.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.ilk,*.ipdb,*.iobj,*.exp,*.lib"; Flags: ignoreversion recursesubdirs createallsubdirs
; LICENSE, LICENSE.txt, THIRD-PARTY-NOTICES.txt and ThirdPartyLicenses are in the published payload.

[Icons]
Name: "{userprograms}\Disturn"; Filename: "{app}\Disturn.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Disturn.ico"
Name: "{userdesktop}\Disturn"; Filename: "{app}\Disturn.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Disturn.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\Disturn.exe"; WorkingDir: "{app}"; Description: "Launch Disturn"; Flags: nowait postinstall skipifsilent
Filename: "ms-settings:taskbar"; Description: "Open taskbar settings to make the volume indicator visible"; Flags: shellexec nowait postinstall unchecked skipifsilent

[Messages]
FinishedLabel=Disturn is installed.%n%nWindows may initially put Disturn's dynamic volume indicator in the hidden-icons menu. In Windows Taskbar settings, expand Other system tray icons (Windows 11) or Select which icons appear on the taskbar (Windows 10), and enable Disturn.

[Code]
#include "UninstallStartupCleanup.iss"
function RunningApplicationError: String;
var
  Locator, Services, Processes: Variant;
begin
  Result := '';
  try
    { Query names only, including hidden windows. Do not inspect user data or
      command lines. Conservatively include development/MSIX instances too. }
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT Name FROM Win32_Process WHERE Name = ''Disturn.exe''');
    if Processes.Count > 0 then
      Result := 'Disturn is still running. Right-click its volume indicator in the system tray and choose Exit, then retry. Closing its window only hides it. Exit any MSIX/development copy too; it will not be uninstalled.';
  except
    Log('Process availability check failed; file changes were blocked.');
    Result := 'Setup could not check whether Disturn is running. Check that Windows Management Instrumentation is available, then retry. No application was terminated.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := RunningApplicationError;
end;

function InitializeUninstall: Boolean;
var
  ErrorText: String;
begin
  Result := False;
  repeat
    ErrorText := RunningApplicationError;
    if ErrorText = '' then
    begin
      Result := True;
      Exit;
    end;
    if UninstallSilent then Exit;
  until MsgBox(ErrorText, mbError, MB_RETRYCANCEL) <> IDRETRY;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { Do not remove startup when the user cancels before uninstalling. }
  if CurUninstallStep = usPostUninstall then
    RemoveOwnedStartupRegistration(
      'Software\Microsoft\Windows\CurrentVersion\Run',
      ExpandConstant('{app}\Disturn.exe'));
end;

{ No Registry, InstallDelete, or UninstallDelete sections. StartupService still
  owns registration during normal operation; uninstall removes only its own Run
  command. Saved settings, migration backups, and MSIX startup remain untouched. }

