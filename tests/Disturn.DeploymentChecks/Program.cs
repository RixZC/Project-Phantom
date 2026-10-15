using Disturn;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var registryBranch = @"Software\Disturn\Stage2Tests\" + Guid.NewGuid().ToString("N");
var assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    assertions++;
    Console.WriteLine("PASS: " + message);
}
string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
string FileAt(string scenario, string name, string contents)
{
    var path = Path.Combine(fixtureRoot, scenario, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, contents);
    return path;
}
string MissingDestination(string scenario) => Path.Combine(fixtureRoot, scenario, "destination", "groups.json");
var id = Guid.Parse("12345678-1234-1234-1234-123456789abc");
var json = $$"""
{
  "SchemaVersion": 6,
  "Groups": [{
    "Id": "{{id}}", "Name": "Fixture Media", "VolumePercent": 64,
    "TrayBackgroundColor": "#2379CC", "TrayTextColor": "#ABCDEF",
    "UseAutomaticTrayTextColor": false, "AccentColor": "#12AB34",
    "Shortcut": {"VirtualKey":38,"Modifiers":0,"PhysicalScanCode":72,"PhysicalIsExtendedKey":false,"PhysicalModifiers":4},
    "Applications": [{"DisplayName":"Fixture App","ProcessName":"Spotify.exe","ExecutablePath":"C:\\Fixture Apps\\Spotify.exe"}]
  }],
  "Settings": {
    "LaunchWithWindows": false, "StartMinimizedToTray": true, "DefaultGroupId": "{{id}}",
    "AutomaticReturnEnabled": true, "ReturnDelaySeconds": 120, "VolumeStepPercent": 7,
    "VolumeUpShortcut":{"VirtualKey":107,"Modifiers":2},
    "VolumeDownShortcut":null
  },
  "UnknownFixtureField": "retained by byte-for-byte migration"
}
""";

// Production identity is derived from the exact manifest identity, not folder guessing.
Check(!DeploymentIdentity.HasPackageIdentity, "test process is unpackaged");
var manifest = XDocument.Load(args[0]);
var identity = manifest.Root!.Elements().Single(e => e.Name.LocalName == "Identity");
Check((string?)identity.Attribute("Name") == DeploymentIdentity.PackageName &&
      (string?)identity.Attribute("Publisher") == DeploymentIdentity.PackagePublisher, "migration identity matches manifest");
Console.WriteLine("Legacy package family: " + DeploymentIdentity.LegacyPackageFamilyName);
var expectedSource = DeploymentIdentity.LegacyPackagedSavePath(fixtureRoot);
Check(expectedSource.Contains(DeploymentIdentity.LegacyPackageFamilyName) &&
      expectedSource.EndsWith(Path.Combine("LocalCache", "Local", "Project Phantom", "groups.json")), "only exact legacy package data path is selected");
Console.WriteLine("Matching package currently registered: " + DeploymentIdentity.IsLegacyPackageRegistered());

try
{
    var executable = FileAt("startup path with spaces", "Disturn.exe", "fixture, never executed");
    var subkey = registryBranch + @"\Run";
    var startup = new UnpackagedStartupRegistration(executable, subkey);
    Check(startup.Read() is { Enabled: false, CanChange: true }, "missing startup entry reads disabled");
    Check(startup.Command == $"\"{executable}\" --startup", "executable path is quoted and startup argument preserved");
    Check(startup.SetEnabled(true) is { Enabled: true, CanChange: true }, "isolated startup registration enables");
    using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
    {
        key.SetValue("UnrelatedFixture", "leave this alone");
        Check((string?)key.GetValue("Disturn") == startup.Command && key.GetValueKind("Disturn") == RegistryValueKind.String,
            "stored startup command and type are correct");
    }
    Check(startup.SetEnabled(true).Enabled == true, "repeated enable is idempotent");
    using (var key = Registry.CurrentUser.OpenSubKey(subkey)!)
        Check(key.GetValueNames().Count(n => n == "Disturn") == 1, "only one Disturn value exists");
    Check(startup.SetEnabled(false).Enabled == false, "isolated startup registration disables");
    using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
    {
        Check((string?)key.GetValue("UnrelatedFixture") == "leave this alone" && key.GetValue("Disturn") is null,
            "disable preserves unrelated values");
        key.SetValue("Disturn", "\"C:\\Another Installation\\Disturn.exe\" --startup");
    }
    Check(startup.SetEnabled(true) is { Enabled: false, CanChange: false }, "foreign startup command cannot be overwritten");
    Check(startup.SetEnabled(false) is { Enabled: false, CanChange: false }, "foreign startup command cannot be removed");
    using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
    {
        Check((string?)key.GetValue("Disturn") == "\"C:\\Another Installation\\Disturn.exe\" --startup", "foreign startup bytes are unchanged");
        key.SetValue("Disturn", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
    }
    Check(!startup.SetEnabled(true).CanChange && !startup.SetEnabled(false).CanChange, "malformed registry value is preserved");
    using (var key = Registry.CurrentUser.OpenSubKey(subkey)!)
        Check(((byte[])key.GetValue("Disturn")!).SequenceEqual(new byte[] { 1, 2, 3 }), "malformed registry bytes are unchanged");
    using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
        key.SetValue("Disturn", startup.Command.Replace("--startup", "--STARTUP"), RegistryValueKind.String);
    Check(!startup.Read().CanChange && !startup.SetEnabled(false).CanChange,
        "argument casing that would bypass startup handling is not silently accepted or deleted");
    using (var key = Registry.CurrentUser.OpenSubKey(subkey, writable: true)!)
        key.SetValue("Disturn", startup.Command, RegistryValueKind.ExpandString);
    Check(!startup.Read().CanChange && !startup.SetEnabled(true).CanChange && !startup.SetEnabled(false).CanChange,
        "non-owned registry value kinds are not converted or removed");
    var missing = new UnpackagedStartupRegistration(Path.Combine(fixtureRoot, "missing.exe"), registryBranch + @"\Missing");
    Check(missing.SetEnabled(true).Enabled == false, "missing executable is not registered");
    var invalid = new UnpackagedStartupRegistration("relative.exe", registryBranch + @"\Invalid");
    Check(invalid.Read() is { Enabled: null, CanChange: false }, "invalid executable path is handled safely");
}
finally
{
    // Only remove the uniquely created fixture branch; never touch the real Run key.
    Registry.CurrentUser.DeleteSubKeyTree(registryBranch, throwOnMissingSubKey: false);
}

var source = FileAt("valid", "packaged/groups.json", json);
var original = Hash(source);
var destination = MissingDestination("valid");
var migrated = GroupPersistenceService.LoadWithMigration(destination, source);
Check(migrated.CanSave && migrated.Groups.Count == 1, "valid packaged data migrates");
Check(Hash(source) == original && Hash(destination) == original, "source and destination bytes are preserved exactly");
var backups = Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.pre-migration.*.bak");
Check(backups.Length == 1 && Hash(backups[0]) == original, "exact backup exists before migration");
Check(migrated.Groups[0].Id == id && migrated.Groups[0].Name == "Fixture Media" && migrated.Groups[0].VolumePercent == 64 &&
    migrated.Groups[0].AccentColor == "#12AB34" && migrated.Groups[0].TrayTextColor == "#ABCDEF" &&
    !migrated.Groups[0].UseAutomaticTrayTextColor, "group identity, volume and all tray colors survive migration");
Check(migrated.Groups[0].Applications.Single().ExecutablePath == @"C:\Fixture Apps\Spotify.exe", "offline application assignment survives migration");
var shortcut = GroupPersistenceService.ReadShortcut(migrated.Groups[0].Shortcut)!;
Check(shortcut.VirtualKey == 38 && shortcut.Modifiers == 0 && shortcut.PhysicalModifiers == 4 && shortcut.Label == "Shift + Num8",
    "registered shortcut and physical display metadata survive migration");
Check(migrated.Settings.DefaultGroupId == id && migrated.Settings.StartMinimizedToTray && migrated.Settings.VolumeStepPercent == 7 &&
    migrated.Settings.ReturnDelaySeconds == 120 && migrated.Settings.AutomaticReturnEnabled &&
    GroupPersistenceService.ReadShortcut(migrated.Settings.VolumeUpShortcut)?.VirtualKey == 107 &&
    migrated.Settings.VolumeDownShortcut.ValueKind == JsonValueKind.Null, "application settings and volume command bindings survive migration");
var repeat = GroupPersistenceService.LoadWithMigration(destination, source);
Check(repeat.CanSave && Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.bak").Length == 1, "migration is not repeated");

var existing = FileAt("existing", "destination/groups.json", json.Replace("Fixture Media", "Destination wins"));
var existingHash = Hash(existing);
Check(GroupPersistenceService.LoadWithMigration(existing, source).Groups[0].Name == "Destination wins" && Hash(existing) == existingHash,
    "existing valid destination is never overwritten");
var malformedDestination = FileAt("bad-destination", "groups.json", "{broken");
var blocked = GroupPersistenceService.LoadWithMigration(malformedDestination, source);
Check(!blocked.CanSave && File.ReadAllText(malformedDestination) == "{broken", "malformed destination is retained without automatic replacement");
foreach (var invalidJson in new[] { "{broken", json.Replace("\"SchemaVersion\": 6", "\"SchemaVersion\": 99") })
{
    var scenario = Guid.NewGuid().ToString("N");
    var invalidSource = FileAt(scenario, "source/groups.json", invalidJson);
    var before = Hash(invalidSource);
    var target = MissingDestination(scenario);
    var result = GroupPersistenceService.LoadWithMigration(target, invalidSource);
    Check(!result.CanSave && result.Warning is not null && !File.Exists(target) && Hash(invalidSource) == before,
        "invalid/unsupported source is preserved and cannot be overwritten by a new empty save");
}
var blockingParent = FileAt("unwritable", "blocking-file", "do not modify");
var failed = GroupPersistenceService.LoadWithMigration(Path.Combine(blockingParent, "groups.json"), source);
Check(!failed.CanSave && failed.Groups.Count == 1 && failed.Groups[0].Name == "Fixture Media" && Hash(source) == original &&
    File.ReadAllText(blockingParent) == "do not modify", "failed migration opens existing data read-only and preserves all files");
var lockedSource = FileAt("locked", "source/groups.json", json);
using (var locked = new FileStream(lockedSource, FileMode.Open, FileAccess.Read, FileShare.None))
{
    var target = MissingDestination("locked");
    Check(!GroupPersistenceService.LoadWithMigration(target, lockedSource).CanSave && !File.Exists(target),
        "inaccessible source blocks saving without creating a destination");
}
var empty = MissingDestination("new-user");
Check(GroupPersistenceService.LoadWithMigration(empty, Path.Combine(fixtureRoot, "absent.json")).CanSave && !File.Exists(empty),
    "new user with no previous data can save normally");
var oldJson = "{\"SchemaVersion\":1,\"Groups\":[{\"Name\":\"Old fixture\",\"Applications\":[]}]}";
var oldFile = FileAt("upgrade", "groups.json", oldJson);
var oldHash = Hash(oldFile);
var upgrade = GroupPersistenceService.LoadWithMigration(oldFile, null);
var upgradeBackups = Directory.GetFiles(Path.GetDirectoryName(oldFile)!, "*.pre-upgrade.*.bak");
Check(upgrade.CanSave && upgrade.NeedsSave && upgrade.Groups[0].Id is not null && upgrade.Groups[0].VolumePercent == 100 &&
    upgradeBackups.Length == 1 && Hash(upgradeBackups[0]) == oldHash && Hash(oldFile) == oldHash,
    "older schema gets an exact backup before existing in-memory upgrade/save behavior");
Check(Hash(source) == original, "original packaged fixture remains unchanged after all checks");
Check(migrated.Settings.TrayIconAppearance == TrayIconAppearance.Automatic,
    "older saves without tray appearance default to Automatic");
Check(TrayIconAppearance.TextArgb(TrayIconAppearance.Light, false) == 0xFF000000 &&
    TrayIconAppearance.TextArgb(TrayIconAppearance.Light, true) == 0xFF000000,
    "Light mode requests opaque black independently of system theme");
Check(TrayIconAppearance.TextArgb(TrayIconAppearance.Dark, true) == 0xFFFFFFFF &&
    TrayIconAppearance.TextArgb(TrayIconAppearance.Dark, false) == 0xFFFFFFFF,
    "Dark mode requests opaque white independently of system theme");
Check(TrayIconAppearance.TextArgb(TrayIconAppearance.Automatic, true) == 0xFF000000 &&
    TrayIconAppearance.TextArgb(TrayIconAppearance.Automatic, false) == 0xFFFFFFFF,
    "Automatic follows the system theme, with dark fallback");
foreach (var invalid in new string?[] { null, "", "Unknown", "light", "#ABCDEF" })
{
    var settings = JsonSerializer.Deserialize<SavedApplicationSettings>(
        JsonSerializer.Serialize(new { TrayIconAppearance = invalid }))!;
    Check(settings.TrayIconAppearance == TrayIconAppearance.Automatic,
        "invalid appearance defaults safely: " + (invalid ?? "null"));
}
foreach (var mode in new[] { TrayIconAppearance.Automatic, TrayIconAppearance.Light, TrayIconAppearance.Dark })
{
    var document = JsonSerializer.Deserialize<SavedGroupsDocument>(json)!;
    document.Settings!.TrayIconAppearance = mode;
    var path = FileAt("appearance-" + mode, "groups.json", JsonSerializer.Serialize(document));
    var restored = GroupPersistenceService.LoadFile(path);
    Check(restored.CanSave && restored.Settings.TrayIconAppearance == mode &&
        restored.Groups[0].Name == "Fixture Media" && restored.Groups[0].VolumePercent == 64 &&
        restored.Groups[0].TrayTextColor == "#ABCDEF" && !restored.Groups[0].UseAutomaticTrayTextColor &&
        restored.Groups[0].AccentColor == "#12AB34" && restored.Groups[0].Applications.Count == 1 &&
        restored.Settings.VolumeStepPercent == 7 && restored.Settings.DefaultGroupId == id &&
        GroupPersistenceService.ReadShortcut(restored.Settings.VolumeUpShortcut)?.VirtualKey == 107,
        mode + " round-trips through disk without changing existing settings/groups");
}

Console.WriteLine($"Completed {assertions} assertions. Fixtures retained at: {fixtureRoot}");

