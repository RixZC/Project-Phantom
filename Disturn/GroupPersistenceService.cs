using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Disturn;

internal static class GroupPersistenceService
{
    private const int CurrentSchemaVersion = 7;
    private const string FileName = "groups.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Keep the legacy logical location and schema. MSIX may redirect this path
    // to private LocalCache data, imported only when the unpackaged file is absent.
    private static string SavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Project Phantom",
        FileName);

    public static GroupLoadResult Load()
    {
        try
        {
            var source = DeploymentIdentity.HasPackageIdentity ? null : DeploymentIdentity.LegacyPackagedSavePath(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            return LoadWithMigration(SavePath, source);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return CannotLoad("The previous package data location could not be verified. Existing files were preserved and saving is disabled.");
        }
    }

    internal static GroupLoadResult LoadWithMigration(string destination, string? source)
    {
        var location = GroupDataMigration.Prepare(destination, source);
        var loaded = LoadFile(location.ReadPath);
        var canSave = location.CanSave && loaded.CanSave;
        var warning = location.Warning ?? loaded.Warning;
        if (canSave && loaded.NeedsSave)
        {
            try { GroupDataMigration.BackupBeforeUpgrade(location.ReadPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                canSave = false;
                warning = "Existing settings were loaded, but a pre-upgrade backup could not be created. The original file was preserved and saving is disabled.";
            }
        }
        return new GroupLoadResult(loaded.Groups, canSave, warning)
        {
            Settings = loaded.Settings,
            NeedsSave = canSave && loaded.NeedsSave
        };
    }

    internal static GroupLoadResult LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return new GroupLoadResult(new List<SavedGroupData>(), canSave: true, warning: null);
        }

        try
        {
            var json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<SavedGroupsDocument>(json, JsonOptions);
            if (document is null || document.SchemaVersion is < 1 or > CurrentSchemaVersion || document.Groups is null)
            {
                return CannotLoad("The saved group file has an unsupported format. The original file was preserved, and changes from this session will not be saved.");
            }

            var settings = document.Settings ?? new SavedApplicationSettings();
            ReadShortcut(settings.VolumeUpShortcut);
            ReadShortcut(settings.VolumeDownShortcut);
            ReadShortcut(settings.MuteShortcut);
            var groups = new List<SavedGroupData>();
            var groupIds = new HashSet<Guid>();
            var needsSave = document.SchemaVersion < CurrentSchemaVersion;
            foreach (var group in document.Groups)
            {
                if (group is null || string.IsNullOrWhiteSpace(group.Name) || group.Applications is null)
                {
                    return CannotLoad("The saved group file contains invalid data. The original file was preserved, and changes from this session will not be saved.");
                }

                var savedGroup = new SavedGroupData
                {
                    Id = group.Id ?? Guid.NewGuid(),
                    Name = group.Name.Trim(),
                    VolumePercent = Math.Clamp(group.VolumePercent, 0, 100),
                    IsMuted = group.IsMuted,
                    TrayBackgroundColor = GroupTrayColors.Normalize(group.TrayBackgroundColor, GroupTrayColors.DefaultBackground),
                    TrayTextColor = GroupTrayColors.Normalize(group.TrayTextColor, GroupTrayColors.DefaultText),
                    UseAutomaticTrayTextColor = group.UseAutomaticTrayTextColor,
                    AccentColor = GroupTrayColors.Normalize(group.AccentColor, GroupTrayColors.DefaultAccent),
                    Shortcut = group.Shortcut
                };
                needsSave |= group.Id is null;
                if (savedGroup.Id == Guid.Empty || !groupIds.Add(savedGroup.Id.Value))
                    return CannotLoad("The saved group file contains invalid or duplicate group IDs. The original file was preserved, and changes from this session will not be saved.");
                // Validate the shape, but retain keys/flags that cannot currently
                // register. Runtime registration failure must not erase settings.
                ReadShortcut(group.Shortcut);
                foreach (var application in group.Applications)
                {
                    if (application is null)
                    {
                        return CannotLoad("The saved group file contains invalid application data. The original file was preserved, and changes from this session will not be saved.");
                    }

                    var executablePath = string.IsNullOrWhiteSpace(application.ExecutablePath)
                        ? null
                        : application.ExecutablePath.Trim();
                    var processName = string.IsNullOrWhiteSpace(application.ProcessName)
                        ? (executablePath is null ? null : Path.GetFileName(executablePath))
                        : application.ProcessName.Trim();

                    if (string.IsNullOrWhiteSpace(processName))
                    {
                        return CannotLoad("The saved group file contains an application without a usable identity. The original file was preserved, and changes from this session will not be saved.");
                    }

                    savedGroup.Applications.Add(new SavedApplicationData
                    {
                        DisplayName = string.IsNullOrWhiteSpace(application.DisplayName)
                            ? processName
                            : application.DisplayName.Trim(),
                        ProcessName = processName,
                        ExecutablePath = executablePath
                    });
                }

                groups.Add(savedGroup);
            }

            if (settings.DefaultGroupId is Guid defaultId && !groupIds.Contains(defaultId))
            {
                settings.DefaultGroupId = null;
                needsSave = true;
            }
            return new GroupLoadResult(groups, canSave: true, warning: null)
            {
                Settings = settings,
                NeedsSave = needsSave
            };
        }
        catch (JsonException)
        {
            return CannotLoad("The saved group file is malformed. The original file was preserved, and changes from this session will not be saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return CannotLoad("The saved group file could not be read. The original file was preserved, and changes from this session will not be saved.");
        }
    }

    public static GroupSaveResult Save(IEnumerable<SavedGroupData> groups, SavedApplicationSettings? settings = null)
    {
        var path = SavePath;
        var directory = Path.GetDirectoryName(path)!;
        var temporaryPath = Path.Combine(directory, $"groups.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            var document = new SavedGroupsDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Groups = groups.ToList(),
                Settings = settings ?? new SavedApplicationSettings()
            };

            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // Replace the old file only after the complete temporary file has been written.
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            return new GroupSaveResult(success: true, warning: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or ArgumentException or NotSupportedException)
        {
            return new GroupSaveResult(
                success: false,
                warning: "Groups changed for this session, but the save file was not updated. Any existing save was left in place.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
                // A leftover temporary file is safe; it is never loaded as the save file.
            }
            catch (UnauthorizedAccessException)
            {
                // A leftover temporary file is safe; it is never loaded as the save file.
            }
            catch (SecurityException)
            {
                // A leftover temporary file is safe; it is never loaded as the save file.
            }
        }
    }

    private static GroupLoadResult CannotLoad(string warning) =>
        new(new List<SavedGroupData>(), canSave: false, warning);

    public static GroupShortcut? ReadShortcut(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        var saved = value.Deserialize<SavedShortcutData>(JsonOptions)
            ?? throw new JsonException("Invalid shortcut data.");
        return new GroupShortcut(saved.VirtualKey, saved.Modifiers)
        {
            PhysicalScanCode = saved.PhysicalScanCode,
            PhysicalIsExtendedKey = saved.PhysicalIsExtendedKey,
            PhysicalModifiers = saved.PhysicalModifiers
        };
    }

    public static JsonElement WriteShortcut(GroupShortcut? shortcut, bool hasShortcutData) =>
        !hasShortcutData ? default : JsonSerializer.SerializeToElement(shortcut is null ? null : new SavedShortcutData
        {
            VirtualKey = shortcut.VirtualKey,
            Modifiers = shortcut.Modifiers,
            PhysicalScanCode = shortcut.PhysicalScanCode,
            PhysicalIsExtendedKey = shortcut.PhysicalIsExtendedKey,
            PhysicalModifiers = shortcut.PhysicalModifiers
        }, JsonOptions);
}

internal sealed class SavedShortcutData
{
    [JsonRequired] public uint VirtualKey { get; set; }
    [JsonRequired] public uint Modifiers { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PhysicalScanCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PhysicalIsExtendedKey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PhysicalModifiers { get; set; }
}

internal sealed class GroupLoadResult
{
    public SavedApplicationSettings Settings { get; init; } = new();
    public bool NeedsSave { get; init; }
    public List<SavedGroupData> Groups { get; }
    public bool CanSave { get; }
    public string? Warning { get; }

    public GroupLoadResult(List<SavedGroupData> groups, bool canSave, string? warning)
    {
        Groups = groups;
        CanSave = canSave;
        Warning = warning;
    }
}

internal sealed class GroupSaveResult
{
    public bool Success { get; }
    public string? Warning { get; }

    public GroupSaveResult(bool success, string? warning)
    {
        Success = success;
        Warning = warning;
    }
}

internal sealed class SavedGroupsDocument
{
    public int SchemaVersion { get; set; }
    public List<SavedGroupData>? Groups { get; set; }
    public SavedApplicationSettings? Settings { get; set; }
}

internal sealed class SavedApplicationSettings
{
    private string _appAppearance = "Automatic";
    public string AppAppearance
    {
        get => _appAppearance;
        set => _appAppearance = value is "Light" or "Dark" ? value : "Automatic";
    }

    private string _trayIconAppearance = Disturn.TrayIconAppearance.Automatic;
    // Additive global setting: old saves default to Automatic; group fields stay intact.
    public string TrayIconAppearance
    {
        get => _trayIconAppearance;
        set => _trayIconAppearance = Disturn.TrayIconAppearance.Normalize(value);
    }

    public bool LaunchWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }
    // Null is Master Volume, independent of group names and list positions.
    public Guid? DefaultGroupId { get; set; }
    public bool AutomaticReturnEnabled { get; set; } = true;
    private int _returnDelaySeconds = 30;
    public int ReturnDelaySeconds
    {
        get => _returnDelaySeconds;
        set => _returnDelaySeconds = value is 10 or 30 or 60 or 120 or 300 ? value : 30;
    }

    private int _volumeStepPercent = 2;
    public int VolumeStepPercent
    {
        get => _volumeStepPercent;
        set => _volumeStepPercent = Math.Clamp(value, 1, 20);
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement VolumeUpShortcut { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement VolumeDownShortcut { get; set; }
    // Missing: first-use media mute default; JSON null: explicitly cleared.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement MuteShortcut { get; set; }
    public bool MuteFeedbackSounds { get; set; } = true;
    private int _feedbackVolumePercent = 35;
    public int FeedbackVolumePercent
    {
        get => _feedbackVolumePercent;
        set => _feedbackVolumePercent = Math.Clamp(value, 0, 100);
    }
}

internal sealed class SavedGroupData
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public int VolumePercent { get; set; } = 100;
    public bool IsMuted { get; set; }
    public string TrayBackgroundColor { get; set; } = GroupTrayColors.DefaultBackground;
    public string TrayTextColor { get; set; } = GroupTrayColors.DefaultText;
    public bool UseAutomaticTrayTextColor { get; set; } = true;
    public string AccentColor { get; set; } = GroupTrayColors.DefaultAccent;
    // Undefined is an older missing field; JSON null explicitly means unbound.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Shortcut { get; set; }
    public List<SavedApplicationData> Applications { get; set; } = new();
}

internal sealed class SavedApplicationData
{
    public string DisplayName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
}

