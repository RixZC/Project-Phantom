; No application install: InitializeSetup runs fixtures and then returns False.
[Setup]
AppName=Disturn Startup Cleanup Tests
AppVersion=1.0
DefaultDirName={tmp}\Disturn-StartupCleanup-Tests
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir=..\..\artifacts\installer-tests
OutputBaseFilename=StartupCleanup.Tests

[Code]
#include "..\UninstallStartupCleanup.iss"

var
  FixtureRoot, RunKey, Executable, Expected: String;
  Checks: Integer;

function TestRegSetValueEx(Key: HKEY; Name: String; Reserved, ValueType: DWORD;
  Data: String; DataBytes: DWORD): LONG;
  external 'RegSetValueExW@advapi32.dll stdcall';

procedure Check(Condition: Boolean; MessageText: String);
begin
  Checks := Checks + 1;
  if not Condition then RaiseException(MessageText);
end;

procedure CheckGuards;
var
  Text: String;
begin
  Check(RegQueryStringValue(HKCU64, RunKey, 'Unrelated', Text), 'Unrelated value missing');
  Check(Text = 'keep-other-startup', 'Unrelated value changed');
  Check(RegQueryStringValue(HKCU64, RunKey, 'PhantomStartup', Text), 'Fixture sentinel missing');
  Check(Text = 'keep-fixture-sentinel', 'Fixture sentinel changed');
end;

procedure CheckStringCase(Command: String; ShouldRemove: Boolean);
var
  Text: String;
begin
  Check(RegWriteStringValue(HKCU64, RunKey, 'Disturn', Command), 'Fixture write failed');
  RemoveOwnedStartupRegistration(RunKey, Executable);
  Check(RegValueExists(HKCU64, RunKey, 'Disturn') <> ShouldRemove, 'Unexpected removal result');
  if not ShouldRemove then
  begin
    Check(RegQueryStringValue(HKCU64, RunKey, 'Disturn', Text), 'Preserved string unreadable');
    Check(Text = Command, 'Preserved command modified');
  end;
  CheckGuards;
end;

procedure CheckRawString(Data: String; ByteCount: DWORD);
var
  Key: HKEY;
  StoredBytes, ValueType: DWORD;
  ShouldRemove: Boolean;
begin
  Check(StartupRegOpenKeyEx(HKCU, RunKey, 0, $0103, Key) = 0, 'Raw fixture open failed');
  try
    Check(TestRegSetValueEx(Key, 'Disturn', 0, 1, Data, ByteCount) = 0, 'Raw fixture write failed');
    StoredBytes := 0;
    Check(StartupRegQueryValueSize(Key, 'Disturn', 0, ValueType, 0, StoredBytes) = 0, 'Fixture size read failed');
    Log('Raw fixture requested bytes=' + IntToStr(ByteCount) + ', stored bytes=' + IntToStr(StoredBytes));
  finally
    StartupRegCloseKey(Key);
  end;
  { On current Windows, RegSetValueEx appends a missing terminator to an otherwise
    complete REG_SZ. Such a stored value is valid, not a malformed fixture.
    Other malformed/oversized cases below must still remain untouched. }
  ShouldRemove := (Data = Expected) and (ByteCount = Length(Expected) * 2) and
    (StoredBytes = ByteCount + 2);
  RemoveOwnedStartupRegistration(RunKey, Executable);
  Check(RegValueExists(HKCU64, RunKey, 'Disturn') <> ShouldRemove,
    'Unexpected raw REG_SZ cleanup result');
  CheckGuards;
end;

procedure RunFixtures;
var
  Number: Cardinal;
  Data: AnsiString;
  Text: String;
begin
  RemoveOwnedStartupRegistration(FixtureRoot + '\Missing', Executable);
  Check(not RegKeyExists(HKCU64, FixtureRoot + '\Missing'), 'Cleanup created a missing key');
  RemoveOwnedStartupRegistration(RunKey, Executable);
  Check(not RegValueExists(HKCU64, RunKey, 'Disturn'), 'Cleanup created a missing value');
  CheckGuards;

  CheckStringCase(Expected, True);
  CheckStringCase('"' + Uppercase(Executable) + '" --startup', True);
  CheckStringCase('  ' + Expected + '  ', True);
  CheckStringCase('"C:\Other Installation\Disturn.exe" --startup', False);
  CheckStringCase('"' + Executable + '.other.exe" --startup', False);
  CheckStringCase('"' + Executable + '"', False);
  CheckStringCase('"' + Executable + '" --STARTUP', False);
  CheckStringCase(Expected + ' --other', False);
  CheckStringCase(Executable + ' --startup', False);
  CheckStringCase('"' + Executable + '"  --startup', False);
  CheckStringCase('"%LOCALAPPDATA%\Programs\Disturn\Disturn.exe" --startup', False);
  CheckStringCase('', False);

  Check(RegWriteDWordValue(HKCU64, RunKey, 'Disturn', 42), 'DWORD fixture write failed');
  RemoveOwnedStartupRegistration(RunKey, Executable);
  Check(RegQueryDWordValue(HKCU64, RunKey, 'Disturn', Number), 'DWORD was removed');
  Check(Number = 42, 'DWORD changed');
  CheckGuards;

  Check(RegWriteExpandStringValue(HKCU64, RunKey, 'Disturn', Expected), 'Expand fixture write failed');
  RemoveOwnedStartupRegistration(RunKey, Executable);
  Check(RegQueryStringValue(HKCU64, RunKey, 'Disturn', Text), 'REG_EXPAND_SZ was removed');
  Check(Text = Expected, 'REG_EXPAND_SZ changed');
  CheckGuards;

  Check(RegWriteBinaryValue(HKCU64, RunKey, 'Disturn', #1#2#3), 'Binary fixture write failed');
  RemoveOwnedStartupRegistration(RunKey, Executable);
  Check(RegQueryBinaryValue(HKCU64, RunKey, 'Disturn', Data), 'Binary was removed');
  Check(Data = #1#2#3, 'Binary changed');
  CheckGuards;

  CheckRawString(Expected, Length(Expected) * 2); { Missing NUL terminator }
  Text := Expected + #0 + 'trailing-data';
  CheckRawString(Text, (Length(Text) + 1) * 2); { Embedded NUL }
  CheckRawString(Expected, Length(Expected) * 2 - 1); { Odd byte count }
  Text := StringOfChar('x', 1100);
  CheckRawString(Text, (Length(Text) + 1) * 2); { Oversized value }
end;

function InitializeSetup: Boolean;
var
  FixtureId, ResultFile, Report: String;
  Index: Integer;
  Created: Boolean;
begin
  Result := False; { This test executable never installs anything. }
  FixtureId := ExpandConstant('{param:FixtureId|}');
  if Length(FixtureId) <> 32 then Exit;
  for Index := 1 to Length(FixtureId) do
    if Pos(FixtureId[Index], '0123456789abcdef') = 0 then Exit;
  FixtureRoot := 'Software\Disturn\InstallerCleanupTests\' + FixtureId;
  RunKey := FixtureRoot + '\Run';
  Executable := 'C:\Fixture Programs\Disturn\Disturn.exe';
  Expected := '"' + Executable + '" --startup';
  ResultFile := ExpandConstant('{srcexe}') + '.results.txt';
  Created := False;
  try
    try
      Check(not RegKeyExists(HKCU64, FixtureRoot), 'Fixture branch already exists');
      Check(RegWriteStringValue(HKCU64, RunKey, 'Unrelated', 'keep-other-startup'), 'Fixture creation failed');
      Created := True;
      Check(RegWriteStringValue(HKCU64, RunKey, 'PhantomStartup', 'keep-fixture-sentinel'), 'Sentinel write failed');
      RunFixtures;
      Report := 'PASS: ' + IntToStr(Checks) + ' checks using the actual shared cleanup code.';
    except
      Report := 'FAIL: ' + GetExceptionMessage;
    end;
  finally
    if Created then
      if not RegDeleteKeyIncludingSubkeys(HKCU64, FixtureRoot) then
        Report := 'FAIL: fixture branch cleanup failed.';
  end;
  SaveStringToFile(ResultFile, Report, False);
end;


