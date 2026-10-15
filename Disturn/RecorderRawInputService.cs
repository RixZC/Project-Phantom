using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace Disturn;

/// <summary>Foreground-only physical input, owned by one open shortcut dialog.</summary>
internal sealed class RecorderRawInputService : IDisposable
{
    private const uint WmInput = 0x00FF, WmNcDestroy = 0x0082, WmActivateApp = 0x001C;
    private static readonly UIntPtr SubclassId = new(3);
    private readonly IntPtr _window;
    private readonly uint _thread = GetCurrentThreadId();
    private readonly Action<PhysicalStroke> _onInput;
    private readonly Action<string> _onError;
    private readonly SubclassProcedure _callback;
    private readonly HashSet<(IntPtr Device, ushort Scan, ushort Prefix)> _pressed = new();
    private readonly Dictionary<(IntPtr Device, ushort Scan, ushort Prefix), uint> _modifiers = new();
    private GCHandle _root;
    private bool _attached, _ownsRegistration, _disposed;

    internal readonly record struct PhysicalStroke(uint ScanCode, bool IsExtended, bool IsDown,
        bool IsModifier, uint Modifiers, bool WindowsHeld);

    public uint Modifiers => _modifiers.Values.Aggregate(0u, (flags, key) => flags | (key switch
    {
        0xA0 or 0xA1 => 4u, 0xA2 or 0xA3 => 2u, 0xA4 or 0xA5 => 1u, _ => 0u
    }));
    private bool WindowsHeld => _modifiers.Values.Any(key => key is 0x5B or 0x5C);

    public RecorderRawInputService(IntPtr window, Action<PhysicalStroke> onInput, Action<string> onError)
    {
        _window = window;
        _onInput = onInput;
        _onError = onError;
        _callback = WindowProcedure;
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out _) != _thread)
            throw new InvalidOperationException("Raw input must attach on the window's owning UI thread.");
        var registrations = GetRegistrations();
        // Refuse to steal another component's target or alter its delivery flags.
        // An identical foreground registration on our HWND can be shared unchanged.
        foreach (var registration in registrations.Where(device => device.UsagePage == 1 && device.Usage is 0 or 6))
        {
            if (registration.Usage != 6 || registration.Target != window || registration.Flags != 0)
                throw new InvalidOperationException("Another component owns raw keyboard input. Its registration was left unchanged.");
        }
        _root = GCHandle.Alloc(_callback);
        if (!SetWindowSubclass(window, _callback, SubclassId, UIntPtr.Zero))
        {
            _root.Free();
            throw new InvalidOperationException("Could not attach the physical shortcut recorder.");
        }
        _attached = true;
        try
        {
            if (!registrations.Any(device => device.UsagePage == 1 && device.Usage == 6))
            {
                // No INPUTSINK, EXINPUTSINK, or NOLEGACY: no background capture
                // and no suppression of WinUI's translated keyboard messages.
                var device = new RawDevice { UsagePage = 1, Usage = 6, Target = window };
                if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RawDevice>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register foreground raw keyboard input.");
                _ownsRegistration = true;
            }
        }
        catch { Dispose(); throw; }
    }

    public void Reset()
    {
        VerifyThread();
        _pressed.Clear();
        _modifiers.Clear();
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr id, UIntPtr data)
    {
        if (!_disposed && message == WmInput && (wParam.ToInt64() & 255) == 0)
        {
            try { ReadKeyboard(lParam); }
            catch (Exception ex) { Reset(); _onError("Physical shortcut input failed: " + ex.Message); }
        }
        else if (!_disposed && message == WmActivateApp && wParam == IntPtr.Zero)
        {
            Reset();
            _onError("Recording stopped because Disturn lost foreground focus. Press modifiers again when recording.");
        }
        else if (message == WmNcDestroy)
        {
            Dispose();
            if (_root.IsAllocated) _root.Free();
        }
        // Forward through the tray/hotkey subclasses and DefWindowProc. The
        // latter performs the required foreground WM_INPUT handle cleanup.
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ReadKeyboard(IntPtr handle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawHeader>();
        if (GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, headerSize) == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (size < headerSize + Marshal.SizeOf<RawKeyboard>() || size > 65536) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var bytesRead = GetRawInputData(handle, 0x10000003, buffer, ref size, headerSize);
            if (bytesRead == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (bytesRead < headerSize + Marshal.SizeOf<RawKeyboard>()) return;
            var header = Marshal.PtrToStructure<RawHeader>(buffer);
            if (header.Type != 1) return;
            var key = Marshal.PtrToStructure<RawKeyboard>(IntPtr.Add(buffer, (int)headerSize));
            ProcessKeyboard(header, key);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void ProcessKeyboard(RawHeader header, RawKeyboard key)
    {
        var scan = key.MakeCode;
        var prefix = (ushort)(key.Flags & 6);
        var down = (key.Flags & 1) == 0;
        if (scan == 0xFF || key.VirtualKey == 0xFF) return;
        var shift = key.VirtualKey is 0x10 or 0xA0 or 0xA1;
        // SCANCODE_SIMULATED can also mark a synthesized AltGr Ctrl event.
        if (key.VirtualKey is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5 && (scan & 0x200) != 0) return;
        // Windows' synthetic numpad Shift events carry a keypad make code
        // instead of a genuine Shift make code. Winit uses the same rule:
        // v0.30.12/src/platform_impl/windows/raw_input.rs, get_keyboard_physical_key.
        if (shift && (IsKeypadScan(scan) || prefix != 0)) return;
        // Pause/PrintScreen prefix sequences are not physical Ctrl/Shift.
        if (prefix == 4 && scan == 0x1D) return;
        var identity = (header.Device, scan, prefix);
        uint modifier = key.VirtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => scan == 0x36 ? 0xA1u : scan == 0x2A ? 0xA0u : 0,
            0x11 or 0xA2 or 0xA3 => prefix == 2 ? 0xA3u : 0xA2u,
            0x12 or 0xA4 or 0xA5 => prefix == 2 ? 0xA5u : 0xA4u,
            0x5B or 0x5C => key.VirtualKey,
            _ => 0
        };
        if (shift && modifier == 0)
            throw new InvalidOperationException("This keyboard supplied an unrecognized physical Shift scan code.");
        if (down)
        {
            if (!_pressed.Add(identity)) return; // Ignore auto-repeat.
            if (modifier != 0) _modifiers[identity] = modifier;
        }
        else
        {
            _pressed.Remove(identity);
            _modifiers.Remove(identity);
        }
        _onInput(new PhysicalStroke(scan, prefix == 2, down, modifier != 0, Modifiers, WindowsHeld));
    }

    private static bool IsKeypadScan(ushort scan) => scan is 0x47 or 0x48 or 0x49 or 0x4B or 0x4C or 0x4D or 0x4F or 0x50 or 0x51 or 0x52 or 0x53;

    private static RawDevice[] GetRegistrations()
    {
        var elementSize = (uint)Marshal.SizeOf<RawDevice>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            var result = GetRegisteredRawInputDevices(null, ref count, elementSize);
            if (result == uint.MaxValue && Marshal.GetLastWin32Error() != 122)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count == 0) return Array.Empty<RawDevice>();
            if (count > 1024) throw new InvalidOperationException("Unexpected raw input registration count.");
            var devices = new RawDevice[count];
            result = GetRegisteredRawInputDevices(devices, ref count, elementSize);
            if (result != uint.MaxValue) return devices.Take((int)result).ToArray();
            if (Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        throw new InvalidOperationException("Raw input registrations changed while opening the recorder.");
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        _disposed = true;
        Reset();
        if (_ownsRegistration)
        {
            try
            {
                var current = GetRegistrations().FirstOrDefault(device => device.UsagePage == 1 && device.Usage == 6);
                if (current.Target == _window && current.Flags == 0)
                {
                    var remove = new RawDevice { UsagePage = 1, Usage = 6, Flags = 1 }; // REMOVE requires null HWND.
                    if (!RegisterRawInputDevices(new[] { remove }, 1, (uint)Marshal.SizeOf<RawDevice>()))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                // A newer registration belongs to another component: leave it intact.
            }
            catch (Exception ex) { _onError("Could not release recorder raw input: " + ex.Message); }
        }
        if (_attached && RemoveWindowSubclass(_window, _callback, SubclassId))
        {
            _attached = false;
            if (_root.IsAllocated) _root.Free();
        }
        else if (_attached)
            _onError("Could not detach the raw recorder; its callback remains rooted until the window closes.");
    }

    private void VerifyThread()
    {
        if (GetCurrentThreadId() != _thread) throw new InvalidOperationException("Raw recorder access must occur on the owning UI thread.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VirtualKey; public uint Message, ExtraInformation; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr SubclassProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRegisteredRawInputDevices([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("comctl32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
