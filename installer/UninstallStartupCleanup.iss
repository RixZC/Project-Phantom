{ Uninstall-only ownership check; never creates a startup registration.
  Native queries retain the registry type and raw REG_SZ terminator checks. }
function StartupRegOpenKeyEx(Key: HKEY; SubKey: String; Options, Access: DWORD;
  var OpenedKey: HKEY): LONG;
  external 'RegOpenKeyExW@advapi32.dll stdcall';
function StartupRegQueryValueEx(Key: HKEY; ValueName: String; Reserved: ULONG_PTR;
  var ValueType: DWORD; Data: String; var DataBytes: DWORD): LONG;
  external 'RegQueryValueExW@advapi32.dll stdcall';
function StartupRegQueryValueSize(Key: HKEY; ValueName: String; Reserved: ULONG_PTR;
  var ValueType: DWORD; Data: ULONG_PTR; var DataBytes: DWORD): LONG;
  external 'RegQueryValueExW@advapi32.dll stdcall';
function StartupRegDeleteValue(Key: HKEY; ValueName: String): LONG;
  external 'RegDeleteValueW@advapi32.dll stdcall';
function StartupRegCloseKey(Key: HKEY): LONG;
  external 'RegCloseKey@advapi32.dll stdcall';

function ReadStartupString(Key: HKEY; var Command: String): Boolean;
var
  Buffer: String;
  ValueType, DataBytes, StoredBytes: DWORD;
begin
  Result := False;
  { Query the stored size first. RegQueryValueEx may append a missing REG_SZ
    terminator if given spare buffer space; do not let that normalize bad data. }
  StoredBytes := 0;
  if StartupRegQueryValueSize(Key, 'Disturn', 0, ValueType, 0, StoredBytes) <> 0 then Exit;
  if (ValueType <> 1) or (StoredBytes < 2) or (StoredBytes > 2048) or
    ((StoredBytes mod 2) <> 0) then Exit;
  Buffer := StringOfChar(#0, StoredBytes div 2);
  DataBytes := StoredBytes;
  if StartupRegQueryValueEx(Key, 'Disturn', 0, ValueType, Buffer, DataBytes) <> 0 then Exit;
  if (ValueType <> 1) or (DataBytes <> StoredBytes) then Exit;
  if Buffer[DataBytes div 2] <> #0 then Exit;
  Command := Copy(Buffer, 1, (DataBytes div 2) - 1);
  if Pos(#0, Command) <> 0 then Exit;
  Result := True;
end;
function IsOwnedStartupCommand(Command, Executable: String): Boolean;
var
  Expected, Suffix: String;
begin
  Command := Trim(Command);
  Expected := '"' + Executable + '" --startup';
  Suffix := ' --startup';
  { Paths are case insensitive; the app's --startup argument is case sensitive. }
  Result := (CompareText(Command, Expected) = 0) and
    (Copy(Command, Length(Command) - Length(Suffix) + 1, Length(Suffix)) = Suffix);
end;

procedure RemoveOwnedStartupRegistration(SubKey, Executable: String);
var
  Key: HKEY;
  Command: String;
  Status: LONG;
begin
  { HKCU, KEY_QUERY_VALUE | KEY_SET_VALUE | KEY_WOW64_64KEY.
    Open only; do not create a missing key, touch HKLM, or change other values. }
  Status := StartupRegOpenKeyEx(HKCU, SubKey, 0, $0103, Key);
  if Status <> 0 then
  begin
    Log('Startup cleanup: key missing or inaccessible; no change.');
    Exit;
  end;
  try
    if not ReadStartupString(Key, Command) then
    begin
      Log('Startup cleanup: missing, unreadable, or malformed value preserved.');
      Exit;
    end;
    if not IsOwnedStartupCommand(Command, Executable) then
    begin
      Log('Startup cleanup: foreign command preserved.');
      Exit;
    end;
    { Re-read on the same open key just before deletion. Windows has no atomic
      compare-and-delete for Run values; a concurrent writer can still race. }
    if not ReadStartupString(Key, Command) then Exit;
    if not IsOwnedStartupCommand(Command, Executable) then Exit;
    if StartupRegDeleteValue(Key, 'Disturn') = 0 then
      Log('Startup cleanup: owned Run value removed.')
    else
      Log('Startup cleanup: value could not be removed; no other changes attempted.');
  finally
    StartupRegCloseKey(Key);
  end;
end;

