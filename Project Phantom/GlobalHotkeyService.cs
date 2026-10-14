using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Project_Phantom;

/// <summary>Owns group shortcut registrations on the existing window's UI thread.</summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmNcDestroy = 0x0082;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(2);
    private readonly IntPtr _windowHandle;
    private readonly uint _owningThread;
    private readonly Action<GroupItem> _selectGroup;
    private readonly Action<string?> _reportWarning;
    private readonly SubclassProcedure _callback;
    private readonly HashSet<int> _registeredIds = new();
    private readonly Dictionary<int, Registration> _registrations = new();
    private readonly HashSet<GroupItem> _knownGroups = new();
    private int _nextId = 1;
    private readonly string? _initializationWarning;
    private GCHandle _callbackRoot;
    private bool _attached;
    private bool _disposed;
    private bool _suspendedForRecording;

    public GlobalHotkeyService(IntPtr windowHandle, Action<GroupItem> selectGroup, Action<string?> reportWarning)
    {
        _windowHandle = windowHandle;
        _owningThread = GetCurrentThreadId();
        _selectGroup = selectGroup;
        _reportWarning = reportWarning;
        _callback = WindowProcedure;
        if (windowHandle == IntPtr.Zero || GetWindowThreadProcessId(windowHandle, out _) != _owningThread)
        {
            _initializationWarning = "Global shortcuts could not attach to the window's owning UI thread.";
            return;
        }

        // Root the delegate until removal (or window destruction), including
        // the unusual case where RemoveWindowSubclass fails during cleanup.
        _callbackRoot = GCHandle.Alloc(_callback);
        _attached = SetWindowSubclass(windowHandle, _callback, SubclassId, UIntPtr.Zero);
        if (!_attached)
        {
            _initializationWarning = "Global shortcuts could not attach to the main window.";
            _callbackRoot.Free();
        }
    }

    public GroupItem? FindOwner(GroupItem target, GroupShortcut shortcut) =>
        _knownGroups.FirstOrDefault(group => !ReferenceEquals(group, target) && group.Shortcut?.IsSameCombination(shortcut) == true);

    public bool Restore(GroupItem group, out string? error)
    {
        _knownGroups.Add(group);
        if (group.Shortcut is null) { error = null; return true; }
        var success = TryAssign(group, group.Shortcut, out error);
        if (!success) group.SetShortcutStatus(false, error);
        return success;
    }

    public void ForgetGroup(GroupItem group)
    {
        VerifyThread();
        _knownGroups.Remove(group);
    }

    public bool TryAssign(GroupItem group, GroupShortcut? shortcut, out string? error, bool allowReassign = false)
    {
        VerifyThread();
        _knownGroups.Add(group);
        error = null;
        var previous = _registrations.Values.FirstOrDefault(item => ReferenceEquals(item.Group, group));
        if (shortcut is null && previous is null)
        {
            group.Shortcut = null;
            group.SetShortcutStatus(false, null);
            return true;
        }
        if (_disposed || !_attached || _suspendedForRecording)
        {
            error = _suspendedForRecording ? "Finish recording before assigning a shortcut."
                : _initializationWarning ?? "Global shortcuts are unavailable.";
            return false;
        }
        if (shortcut?.ValidationError is string validationError)
        {
            error = validationError;
            return false;
        }

        var owner = shortcut is null ? null : FindOwner(group, shortcut);
        if (owner is not null && !allowReassign)
        {
            error = $"{shortcut!.Label} is already assigned to '{owner.Name}'.";
            return false;
        }
        if (previous is not null && shortcut is not null && previous.Shortcut.IsSameCombination(shortcut) &&
            _registeredIds.Contains(previous.Id) && owner is null)
        {
            _registrations[previous.Id] = previous with { Shortcut = shortcut };
            group.Shortcut = shortcut;
            group.SetShortcutStatus(true, null);
            return true;
        }

        var incoming = owner is null ? null : _registrations.Values.FirstOrDefault(item => ReferenceEquals(item.Group, owner));
        if (incoming is not null && !_registeredIds.Contains(incoming.Id)) incoming = null;
        // A confirmed transfer of an active registration only needs its callback
        // target changed. The native combination remains registered throughout.
        var newId = incoming?.Id ?? 0;
        var newlyRegistered = false;
        if (shortcut is not null && incoming is null)
        {
            if (_nextId > 0xBFFF)
            {
                error = "This session has exhausted shortcut registration IDs. Restart Phantom.";
                return false;
            }
            newId = _nextId++;
            if (!RegisterHotKey(_windowHandle, newId, shortcut.Modifiers | ModNoRepeat, shortcut.VirtualKey))
            {
                error = ErrorMessage($"Could not register {shortcut.Label}; another app or Windows may already use it",
                    Marshal.GetLastWin32Error());
                return false;
            }
            _registeredIds.Add(newId);
            newlyRegistered = true;
        }
        // Registration succeeds before touching either group's configuration.
        if (previous is not null && previous.Id != newId)
        {
            if (_registeredIds.Contains(previous.Id) && !UnregisterHotKey(_windowHandle, previous.Id))
            {
                error = ErrorMessage($"Could not release {previous.Shortcut.Label}; both assignments were preserved",
                    Marshal.GetLastWin32Error());
                if (newlyRegistered)
                {
                    if (UnregisterHotKey(_windowHandle, newId)) _registeredIds.Remove(newId);
                    else error += " The replacement could not be released; it is inactive in Phantom and will be retried during Exit.";
                }
                return false;
            }
            _registeredIds.Remove(previous.Id);
            _registrations.Remove(previous.Id);
        }
        if (owner is not null)
        {
            foreach (var id in _registrations.Where(pair => ReferenceEquals(pair.Value.Group, owner)).Select(pair => pair.Key).ToArray())
                _registrations.Remove(id);
            owner.Shortcut = null;
            owner.SetShortcutStatus(false, null);
        }
        if (shortcut is not null) _registrations[newId] = new Registration(newId, group, shortcut);
        group.Shortcut = shortcut;
        group.SetShortcutStatus(shortcut is not null, null);
        return true;
    }
    private sealed record Registration(int Id, GroupItem Group, GroupShortcut Shortcut);

    public bool TrySuspendForRecording(out string? error)
    {
        VerifyThread();
        error = null;
        if (_disposed || !_attached)
        {
            error = _initializationWarning ?? "Global shortcuts are unavailable.";
            return false;
        }
        if (_suspendedForRecording) return true;
        _suspendedForRecording = true;
        foreach (var registration in _registrations.Values)
        {
            if (!_registeredIds.Contains(registration.Id)) continue;
            if (!UnregisterHotKey(_windowHandle, registration.Id))
            {
                error = ErrorMessage("Could not pause shortcuts for recording", Marshal.GetLastWin32Error());
                TryResumeAfterRecording(out var restoreError);
                if (restoreError is not null) error += " " + restoreError;
                return false;
            }
            _registeredIds.Remove(registration.Id);
            registration.Group.SetShortcutStatus(false, "Paused while recording.");
        }
        return true;
    }

    public bool TryResumeAfterRecording(out string? error)
    {
        VerifyThread();
        error = null;
        if (!_suspendedForRecording || _disposed) return true;
        var warnings = new List<string>();
        foreach (var registration in _registrations.Values)
        {
            if (_registeredIds.Contains(registration.Id)) continue;
            if (RegisterHotKey(_windowHandle, registration.Id, registration.Shortcut.Modifiers | ModNoRepeat,
                registration.Shortcut.VirtualKey))
            {
                _registeredIds.Add(registration.Id);
                registration.Group.SetShortcutStatus(true, null);
            }
            else
            {
                var warning = ErrorMessage($"Could not restore {registration.Shortcut.Label}; it is currently inactive",
                    Marshal.GetLastWin32Error());
                registration.Group.SetShortcutStatus(false, warning);
                warnings.Add(warning);
            }
        }
        _suspendedForRecording = false;
        error = warnings.Count == 0 ? null : string.Join(" ", warnings);
        return error is null;
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmNcDestroy)
        {
            _disposed = true;
            _registeredIds.Clear(); // Window-bound hotkeys end with this HWND.
            _registrations.Clear();
            _knownGroups.Clear();
            RemoveWindowSubclass(window, _callback, SubclassId);
            _attached = false;
            if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        }
        else if (!_disposed && !_suspendedForRecording && message == WmHotkey)
        {
            var id = unchecked((int)wParam.ToInt64());
            if (_registeredIds.Contains(id) && _registrations.TryGetValue(id, out var registration))
            {
                // WM_HOTKEY arrives on the HWND's owning UI thread. No dispatch
                // delay, window activation, or audio-control call is needed.
                try { _selectGroup(registration.Group); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Global shortcut selection failed: {ex}");
                    _reportWarning("The global shortcut could not select its group.");
                }
                return IntPtr.Zero;
            }
        }
        // Preserve the tray subclass and WinUI's normal message processing.
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        _disposed = true;
        var warnings = new List<string>();
        foreach (var id in _registeredIds)
        {
            if (!UnregisterHotKey(_windowHandle, id))
                warnings.Add(ErrorMessage("Could not release a global shortcut", Marshal.GetLastWin32Error()));
        }
        _registeredIds.Clear();
        _registrations.Clear();
        foreach (var group in _knownGroups) group.SetShortcutStatus(false, "Phantom is exiting.");
        _knownGroups.Clear();
        if (_attached)
        {
            if (RemoveWindowSubclass(_windowHandle, _callback, SubclassId))
            {
                _attached = false;
                if (_callbackRoot.IsAllocated) _callbackRoot.Free();
            }
            else
                warnings.Add("Could not detach global shortcuts; the callback remains rooted until the window closes.");
        }
        if (warnings.Count > 0)
        {
            var warning = string.Join(" ", warnings);
            Debug.WriteLine(warning);
            _reportWarning(warning);
        }
    }

    private void VerifyThread()
    {
        if (GetCurrentThreadId() != _owningThread)
            throw new InvalidOperationException("Global shortcuts must be managed on the window's owning UI thread.");
    }

    private static string ErrorMessage(string action, int error) =>
        $"{action}: {new Win32Exception(error).Message} (Windows error {error}).";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
