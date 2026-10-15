using System;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace Disturn;

/// <summary>Copies only a known Disturn save into a missing destination; never replaces or deletes source data.</summary>
internal static class GroupDataMigration
{
    internal sealed record Location(string ReadPath, bool CanSave = true, string? Warning = null);

    internal static Location Prepare(string destination, string? source)
    {
        string? temporary = null;
        var sourceTrusted = false;
        try
        {
            if (IsPresent(destination))
            {
                Trace.WriteLine("[DataMigration] Existing destination retained.");
                return new Location(destination);
            }
            if (source is null || !IsPresent(source))
            {
                Trace.WriteLine("[DataMigration] No previous save found.");
                return new Location(destination);
            }
            RejectReparsePoints(source);
            sourceTrusted = true;
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!GroupPersistenceService.LoadFile(source).CanSave)
            {
                Trace.WriteLine("[DataMigration] Source validation failed; saving blocked.");
                return new Location(source, false,
                    "Existing packaged settings are malformed or unsupported. They were preserved. Automatic migration and saving are disabled; back up and review the original file before importing it.");
            }
            var directory = Path.GetDirectoryName(destination)!;
            RejectReparsePoints(directory);
            Directory.CreateDirectory(directory);
            // Keep an exact snapshot before creating the destination. The source remains locked against writers/deletion.
            CopyAndFlush(input, destination + $".pre-migration.{Guid.NewGuid():N}.bak");
            input.Position = 0;
            temporary = Path.Combine(directory, $"groups.migration.{Guid.NewGuid():N}.tmp");
            CopyAndFlush(input, temporary);
            // Same-directory, non-overwriting move: a competing destination always wins.
            File.Move(temporary, destination);
            Trace.WriteLine("[DataMigration] Backup created and source copied successfully.");
            return new Location(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            // Another process may have created a destination after our initial check.
            if (File.Exists(destination) && GroupPersistenceService.LoadFile(destination).CanSave)
            {
                Trace.WriteLine("[DataMigration] Concurrent valid destination retained.");
                return new Location(destination);
            }
            Trace.WriteLine("[DataMigration] Migration unavailable; existing data retained and saving blocked.");
            return new Location(sourceTrusted ? source! : destination, false,
                "Previous settings could not be safely migrated. No source data was deleted or replaced. Available previous settings are opened read-only; saving is disabled until the issue is resolved.");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }
    }

    internal static void BackupBeforeUpgrade(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CopyAndFlush(input, path + $".pre-upgrade.{Guid.NewGuid():N}.bak");
        Trace.WriteLine("[DataMigration] Pre-upgrade backup created.");
    }

    private static void CopyAndFlush(Stream input, string destination)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static bool IsPresent(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("Expected a save file.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RejectReparsePoints(string path)
    {
        // Do not follow junctions/symlinks into an unrelated application's data.
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Redirected migration paths require manual review.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
