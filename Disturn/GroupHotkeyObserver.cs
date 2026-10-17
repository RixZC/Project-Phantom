using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Disturn;

/// <summary>Pass-through group selection, with conditional consumption only for the assigned mute command.</summary>
internal sealed class GroupHotkeyObserver : IDisposable
{
    internal readonly record struct Binding(int Id, uint VirtualKey, uint Modifiers, bool Suppress = false);
    private sealed record Snapshot(Binding[] Bindings, int Version);
    private Snapshot _snapshot = new(Array.Empty<Binding>(), 0);
    private readonly Func<int, int, bool> _onPressed;
    private readonly Action<string> _onError;
    private readonly HookProcedure _callback;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private int _startError;
    private bool _disposed;
    private int _failed;
    // Ephemeral held flags only, cleared on thread exit. No keystroke history.
    private readonly bool[] _down = new bool[256];
    private readonly bool[] _suppressed = new bool[256];

    internal GroupHotkeyObserver(Func<int, int, bool> onPressed, Action<string> onError)
    {
        _onPressed = onPressed;
        _onError = onError;
        _callback = Observe;
    }

    // Lifecycle and snapshot changes are managed only by GlobalHotkeyService's UI thread.
    internal bool TryStart(out int error)
    {
        if (_disposed) { error = 6; return false; }
        if (_thread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Disturn group shortcut observer" };
            _thread.Start();
            _ready.Wait();
        }
        error = _startError;
        if (error == 0 && (!_thread.IsAlive || Volatile.Read(ref _failed) != 0)) error = 31;
        return error == 0;
    }

    internal void Update(Binding[] bindings, int version) =>
        Volatile.Write(ref _snapshot, new Snapshot(bindings, version));

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        // Ensure a message queue exists before the UI can request shutdown.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        // Initialize held state once. Subsequent state comes from hook events,
        // since async state has not yet been updated inside the hook callback.
        for (var key = 0; key < _down.Length; key++)
            _down[key] = (GetAsyncKeyState(key) & 0x8000) != 0;
        // Track only sided modifiers, avoiding stale generic flags when the
        // hook subsequently reports VK_LSHIFT/VK_RSHIFT (and Ctrl/Alt equivalents).
        _down[0x10] = _down[0x11] = _down[0x12] = false;
        var hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
        if (hook == IntPtr.Zero) _startError = Marshal.GetLastWin32Error();
        _ready.Set();
        if (hook == IntPtr.Zero) return;
        try
        {
            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            if (result < 0)
            {
                Volatile.Write(ref _failed, 1);
                _onError("Group shortcut observation stopped: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }
        finally
        {
            if (!UnhookWindowsHookEx(hook))
                _onError("Could not release the group shortcut observer; its thread is exiting.");
            Array.Clear(_down);
            Array.Clear(_suppressed);
            // Keep the native delegate rooted until after unhooking/thread cleanup.
            GC.KeepAlive(_callback);
        }
    }

    private IntPtr Observe(int code, IntPtr wParam, IntPtr lParam)
    {
        var suppress = false;
        try
        {
            if (code == 0)
            {
                var message = unchecked((uint)wParam.ToInt64());
                if (message is 0x100 or 0x104 or 0x101 or 0x105)
                {
                    var input = Marshal.PtrToStructure<KeyboardInput>(lParam);
                    if (input.VirtualKey < _down.Length)
                    {
                        var key = (int)input.VirtualKey;
                        key = key switch
                        {
                            0x10 => input.ScanCode == 0x36 ? 0xA1 : 0xA0,
                            0x11 => (input.Flags & 1) != 0 ? 0xA3 : 0xA2,
                            0x12 => (input.Flags & 1) != 0 ? 0xA5 : 0xA4,
                            _ => key
                        };
                        var pressed = message is 0x100 or 0x104;
                        var repeated = _down[key];
                        _down[key] = pressed;
                        if (_suppressed[key])
                        {
                            // Keep repeats/key-up paired with an already consumed mute press,
                            // even if selection or assignments changed while the key was held.
                            suppress = true;
                            if (!pressed) _suppressed[key] = false;
                        }
                        if (pressed && !repeated && !suppress)
                        {
                            var modifiers = (_down[0xA2] || _down[0xA3] ? 2u : 0) |
                                (_down[0xA0] || _down[0xA1] ? 4u : 0) |
                                (_down[0xA4] || _down[0xA5] ? 1u : 0) |
                                (_down[0x5B] || _down[0x5C] ? 8u : 0);
                            var snapshot = Volatile.Read(ref _snapshot);
                            foreach (var binding in snapshot.Bindings)
                            {
                                if (binding.VirtualKey != input.VirtualKey || binding.Modifiers != modifiers) continue;
                                var queued = _onPressed(binding.Id, snapshot.Version);
                                if (binding.Suppress && queued)
                                {
                                    _suppressed[key] = true;
                                    suppress = true;
                                }
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Never let managed errors escape a native callback or consume input.
            Volatile.Write(ref _failed, 1);
            Volatile.Write(ref _snapshot, new Snapshot(Array.Empty<Binding>(), -1));
            _onError("Group shortcut observation failed. Restart Disturn to retry.");
        }
        // Group selections and unrelated keys always pass through. Only a queued,
        // explicitly consuming mute binding (and its paired repeats/up) is swallowed.
        return suppress ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _snapshot, new Snapshot(Array.Empty<Binding>(), -1));
        if (_thread?.IsAlive == true)
        {
            if (!PostThreadMessage(_threadId, 0x12, UIntPtr.Zero, IntPtr.Zero)) // WM_QUIT
            {
                _onError("Could not stop the group shortcut observer; observation is disabled until process exit.");
                return;
            }
            _thread.Join();
        }
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, HookProcedure callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);
}
