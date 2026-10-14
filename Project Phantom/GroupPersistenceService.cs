using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;

namespace Project_Phantom;

internal static class GroupPersistenceService
{
    private const int CurrentSchemaVersion = 3;
    private const string FileName = "groups.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static string SavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Project Phantom",
        FileName);

    public static GroupLoadResult Load()
    {
        var path = SavePath;
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

            var groups = new List<SavedGroupData>();
            foreach (var group in document.Groups)
            {
                if (group is null || string.IsNullOrWhiteSpace(group.Name) || group.Applications is null)
                {
                    return CannotLoad("The saved group file contains invalid data. The original file was preserved, and changes from this session will not be saved.");
                }

                var savedGroup = new SavedGroupData
                {
                    Name = group.Name.Trim(),
                    VolumePercent = Math.Clamp(group.VolumePercent, 0, 100),
                    TrayBackgroundColor = GroupTrayColors.Normalize(group.TrayBackgroundColor, GroupTrayColors.DefaultBackground),
                    TrayTextColor = GroupTrayColors.Normalize(group.TrayTextColor, GroupTrayColors.DefaultText),
                    UseAutomaticTrayTextColor = group.UseAutomaticTrayTextColor,
                    AccentColor = GroupTrayColors.Normalize(group.AccentColor, GroupTrayColors.DefaultAccent)
                };
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

            return new GroupLoadResult(groups, canSave: true, warning: null);
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

    public static GroupSaveResult Save(IEnumerable<SavedGroupData> groups)
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
                Groups = groups.ToList()
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
}

internal sealed class GroupLoadResult
{
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
}

internal sealed class SavedGroupData
{
    public string Name { get; set; } = "";
    public int VolumePercent { get; set; } = 100;
    public string TrayBackgroundColor { get; set; } = GroupTrayColors.DefaultBackground;
    public string TrayTextColor { get; set; } = GroupTrayColors.DefaultText;
    public bool UseAutomaticTrayTextColor { get; set; } = true;
    public string AccentColor { get; set; } = GroupTrayColors.DefaultAccent;
    public List<SavedApplicationData> Applications { get; set; } = new();
}

internal sealed class SavedApplicationData
{
    public string DisplayName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string? ExecutablePath { get; set; }
}
