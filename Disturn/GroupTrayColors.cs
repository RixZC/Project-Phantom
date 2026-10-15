using System;
using System.Globalization;

namespace Disturn;

internal static class GroupTrayColors
{
    public const string DefaultBackground = "#2379CC";
    public const string DefaultText = "#FFFFFF";
    public const string DefaultAccent = "#4B9FFF";

    // Keep opaque RGB settings independent of WinUI and native icon resources.
    public static string Normalize(string? value, string fallback)
    {
        var color = value?.Trim();
        return color is { Length: 7 } && color[0] == '#' &&
            uint.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
            ? color.ToUpperInvariant() : fallback;
    }

    public static string AutomaticText(string background)
    {
        var rgb = uint.Parse(background.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        static double Linear(uint channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear((rgb >> 16) & 255) +
            0.7152 * Linear((rgb >> 8) & 255) + 0.0722 * Linear(rgb & 255);
        var blackContrast = (luminance + 0.05) / 0.05;
        var whiteContrast = 1.05 / (luminance + 0.05);
        return whiteContrast >= blackContrast ? "#FFFFFF" : "#000000";
    }
}
