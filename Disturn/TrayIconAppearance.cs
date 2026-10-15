using Microsoft.Win32;
using System;
using System.IO;
using System.Security;

namespace Disturn;

/// <summary>Taskbar appearance only; independent of the application's XAML theme.</summary>
internal static class TrayIconAppearance
{
    internal const string Automatic = "Automatic";
    internal const string Light = "Light";
    internal const string Dark = "Dark";

    internal static string Normalize(string? mode) => mode is Light or Dark ? mode : Automatic;

    internal static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // Do not use AppsUseLightTheme: Windows allows different app/taskbar modes.
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false; // Missing/inaccessible information falls back to dark.
        }
    }

    internal static uint TextArgb(string? mode, bool systemUsesLightTheme) =>
        Normalize(mode) switch
        {
            Light => 0xFF000000,
            Dark => 0xFFFFFFFF,
            _ => systemUsesLightTheme ? 0xFF000000 : 0xFFFFFFFF
        };
}
