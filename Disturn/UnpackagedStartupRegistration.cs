using Microsoft.Win32;
using System;
using System.IO;
using System.Security;

namespace Disturn;

/// <summary>One owned per-user Run value. The injectable subkey keeps tests out of the real Run key.</summary>
internal sealed class UnpackagedStartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Disturn";
    private readonly string? _executable;
    private readonly string _subkey;
    internal sealed record Status(bool? Enabled, bool CanChange, string? Warning);

    internal UnpackagedStartupRegistration(string? executable, string subkey = RunKey)
    {
        _executable = executable;
        _subkey = subkey;
    }

    internal string Command
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_executable) || !Path.IsPathFullyQualified(_executable) ||
                _executable.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 ||
                !string.Equals(Path.GetExtension(_executable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The application executable path is unavailable or invalid.");
            var command = $"\"{Path.GetFullPath(_executable)}\" --startup";
            if (command.Length > 260) throw new ArgumentException("The installed path is too long for a Windows Run command.");
            return command;
        }
    }

    internal Status Read()
    {
        try
        {
            var command = Command;
            using var key = Registry.CurrentUser.OpenSubKey(_subkey);
            return Read(key, command);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return new Status(null, false, "The current-user startup registration could not be read. No registration was changed.");
        }
    }

    private static Status Read(RegistryKey? key, string command)
    {
        if (key is null || !Array.Exists(key.GetValueNames(), name =>
            string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase)))
            return new Status(false, true, null);
        var value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key.GetValueKind(ValueName) != RegistryValueKind.String || value is not string text ||
            !string.Equals(text.Trim(), command, StringComparison.OrdinalIgnoreCase) ||
            !text.Trim().EndsWith(" --startup", StringComparison.Ordinal))
            return new Status(false, false,
                "A startup entry named Disturn belongs to another installation or contains an unexpected command. It was left unchanged.");
        return new Status(true, true,
            "Registered for your Windows sign-in. Windows Settings or policy may separately disable startup; check Settings > Apps > Startup if needed.");
    }

    internal Status SetEnabled(bool enabled)
    {
        try
        {
            var command = Command;
            // Do not create a key just to disable an absent value.
            using var key = enabled ? Registry.CurrentUser.CreateSubKey(_subkey) : Registry.CurrentUser.OpenSubKey(_subkey, writable: true);
            var before = Read(key, command);
            if (!before.CanChange) return before;
            if (enabled)
            {
                if (!File.Exists(_executable))
                    return before with { Warning = "The executable is unavailable. Startup was left unchanged." };
                key!.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else if (before.Enabled == true)
            {
                // Recheck ownership on the same key immediately before removal.
                if (!Read(key, command).CanChange) return Read(key, command);
                key!.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            var actual = Read(key, command);
            return actual.Enabled == enabled ? actual : actual with { Warning = "Windows did not apply the requested startup registration change." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return Read() with { Warning = "The current-user startup registration could not be changed. Existing entries were preserved where possible." };
        }
    }
}
