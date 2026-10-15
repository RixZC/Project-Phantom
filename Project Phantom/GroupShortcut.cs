using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Project_Phantom;

/// <summary>A durable virtual-key/modifier identity, independent of native registration IDs.</summary>
public sealed record GroupShortcut(uint VirtualKey, uint Modifiers = 0)
{
    // Display metadata only. RegisterHotKey and conflict identity still use VK/modifiers.
    public uint? PhysicalScanCode { get; init; }
    public bool? PhysicalIsExtendedKey { get; init; }
    public uint? PhysicalModifiers { get; init; }
    public GroupShortcut(uint key, bool control, bool shift, bool alt)
        : this(key, (control ? 2u : 0) | (shift ? 4u : 0) | (alt ? 1u : 0)) { }
    public bool Control => (Modifiers & 2) != 0;
    public bool Shift => (Modifiers & 4) != 0;
    public bool Alt => (Modifiers & 1) != 0;

    public static bool IsNavigationKey(uint key) => key is >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    public bool IsSameCombination(GroupShortcut? other) =>
        other is not null && VirtualKey == other.VirtualKey && Modifiers == other.Modifiers;

    private (string Label, uint Key)? PhysicalNumpadKey
    {
        get
        {
            if (PhysicalScanCode is not uint scan || PhysicalIsExtendedKey is not bool extended) return null;
            if (extended)
                return (scan, VirtualKey) switch
                {
                    (0x1C, 0x0D) => ("Numpad Enter", 0x0D),
                    (0x35, 0x6F) => ("Numpad Divide", 0x6F),
                    _ => null
                };
            // Dedicated navigation keys are extended; a nonextended keypad
            // scan code remains identifiable when Num Lock/Shift translates VK.
            if (VirtualKey is not (>= 0x60 and <= 0x6F or >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x0C)) return null;
            return scan switch
            {
                0x52 => ("Num0", 0x60), 0x4F => ("Num1", 0x61),
                0x50 => ("Num2", 0x62), 0x51 => ("Num3", 0x63),
                0x4B => ("Num4", 0x64), 0x4C => ("Num5", 0x65),
                0x4D => ("Num6", 0x66), 0x47 => ("Num7", 0x67),
                0x48 => ("Num8", 0x68), 0x49 => ("Num9", 0x69),
                0x53 => ("Numpad Decimal", 0x6E), 0x37 => ("Numpad Multiply", 0x6A),
                0x4A => ("Numpad Subtract", 0x6D), 0x4E => ("Numpad Add", 0x6B),
                _ => null
            };
        }
    }

    public bool IsPhysicalNumpad => PhysicalNumpadKey is not null;
    private uint DisplayModifiers => PhysicalModifiers is uint physical && (physical & ~7u) == 0
        ? physical : Modifiers;

    public string? TranslationWarning => DisplayModifiers != Modifiers || (PhysicalNumpadKey is { } physical &&
        (physical.Key != VirtualKey || physical.Label == "Numpad Enter"))
        ? $"Windows interprets this as {RegisteredLabel}. This shortcut may also activate from other keys."
        : null;

    public string? ValidationError
    {
        get
        {
            if (VirtualKey == 0x7B) return "F12 is reserved for debuggers and cannot be assigned.";
            if (VirtualKey == 0x2E && Control && Alt) return "Ctrl+Alt+Delete is reserved by Windows.";
            if ((Modifiers & ~7u) != 0) return "Only Ctrl, Shift, and Alt modifier flags are supported.";
            if (VirtualKey is 0 or >= 0xFF or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
                return "Record a non-modifier key with a valid Windows virtual-key code.";
            return null;
        }
    }

    public string? Warning => !Control && !Alt && VirtualKey is not (>= 0x70 and <= 0x87)
        ? "This global shortcut may intercept normal typing, navigation, or game controls. You can still save it."
        : "This combination may replace a Windows, application, or game command. Windows will decide whether it can be registered.";

    public string Label => FormatLabel(PhysicalNumpadKey?.Label ?? KeyLabel(VirtualKey), DisplayModifiers);
    public string RegisteredLabel => FormatLabel(KeyLabel(VirtualKey), Modifiers);

    private static string FormatLabel(string keyLabel, uint modifiers)
    {
            var parts = new List<string>();
            if ((modifiers & 2) != 0) parts.Add("Ctrl");
            if ((modifiers & 4) != 0) parts.Add("Shift");
            if ((modifiers & 1) != 0) parts.Add("Alt");
            if ((modifiers & ~7u) != 0) parts.Add($"Flags 0x{modifiers & ~7u:X}");
            parts.Add(keyLabel);
            return string.Join(" + ", parts);
    }

    public static string KeyLabel(uint key) => key switch
    {
        >= 0x70 and <= 0x87 => $"F{key - 0x70 + 1}",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => $"Num{key - 0x60}",
        0x6A => "NumMultiply", 0x6B => "NumAdd", 0x6C => "NumSeparator",
        0x6D => "NumSubtract", 0x6E => "NumDecimal", 0x6F => "NumDivide",
        0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2D => "Insert", 0x2E => "Delete",
        0x08 => "Backspace", 0x09 => "Tab", 0x0C => "Clear", 0x0D => "Enter",
        0x1B => "Esc", 0x20 => "Space", 0x13 => "Pause", 0x14 => "Caps Lock",
        0x2C => "Print Screen", 0x90 => "Num Lock", 0x91 => "Scroll Lock",
        0xAF => "Volume Up", 0xAE => "Volume Down", 0xAD => "Volume Mute",
        _ => NativeKeyLabel(key)
    };

    private static string NativeKeyLabel(uint key)
    {
        var scan = MapVirtualKey(key, 4); // VK_TO_VSC_EX: retain extended-key information.
        var parameter = (int)((scan & 255) << 16);
        if ((scan & 0xFF00) != 0) parameter |= 1 << 24;
        var name = new StringBuilder(64);
        return scan != 0 && GetKeyNameText(parameter, name, name.Capacity) > 0 ? name.ToString() : $"VK 0x{key:X2}";
    }

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW", ExactSpelling = true)]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", EntryPoint = "GetKeyNameTextW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int parameter, StringBuilder name, int size);
}
