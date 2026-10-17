using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Disturn;

internal enum VolumeCommand { Up, Down, Mute }

/// <summary>Owns group and volume-command shortcuts on the existing window's UI thread.</summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr SubclassId = new(2);
    private readonly IntPtr _windowHandle;
    private readonly uint _owningThread;
    private readonly Action<GroupItem> _selectGroup;
    private readonly Action<int> _adjustVolume;
    private readonly Action _toggleMute;
    private bool _hasSelectedGroup;
    private readonly Action<string?> _reportWarning;
    private readonly SubclassProcedure _callback;
    private readonly DispatcherQueue _dispatcher;
    private readonly GroupHotkeyObserver _groupObserver;
    private readonly Dictionary<int, (GroupShortcut Shortcut, bool Suppress)> _observedShortcuts = new();
    private int _observerVersion;
    private readonly HashSet<int> _registeredIds = new();
    private readonly Dictionary<int, Registration> _registrations = new();
    private readonly HashSet<GroupItem> _knownGroups = new();
    private readonly Dictionary<VolumeCommand, GroupShortcut?> _volumeShortcuts = new()
    {
        [VolumeCommand.Up] = null, [VolumeCommand.Down] = null, [VolumeCommand.Mute] = null
    };
    private readonly Dictionary<VolumeCommand, string?> _volumeErrors = new();
    public event Action? StatusChanged;
    private int _nextId = 1;
    private readonly string? _initializationWarning;
    private GCHandle _callbackRoot;
    private bool _attached;
    private bool _disposed;
    private bool _suspendedForRecording;

    public GlobalHotkeyService(IntPtr windowHandle, Action<GroupItem> selectGroup, Action<string?> reportWarning,
        Action<int> adjustVolume, Action toggleMute)
    {
        _windowHandle = windowHandle;
        _owningThread = GetCurrentThreadId();
        _selectGroup = selectGroup;
        _adjustVolume = adjustVolume;
        _toggleMute = toggleMute;
        _reportWarning = reportWarning;
        _callback = WindowProcedure;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Global shortcuts require a UI dispatcher.");
        _groupObserver = new GroupHotkeyObserver(QueueObservedShortcut, warning =>
            _dispatcher.TryEnqueue(() => ReportObserverFailure(warning)));
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

    public string? FindConflictName(GroupItem? targetGroup, VolumeCommand? targetCommand, GroupShortcut shortcut)
    {
        var owner = FindConflict(targetGroup, targetCommand, shortcut);
        return owner is null ? null : OwnerName(owner.Value.Group, owner.Value.Command);
    }

    private (GroupItem? Group, VolumeCommand? Command)? FindConflict(
        GroupItem? targetGroup, VolumeCommand? targetCommand, GroupShortcut shortcut)
    {
        var group = _knownGroups.FirstOrDefault(item => !ReferenceEquals(item, targetGroup) &&
            item.Shortcut?.IsSameCombination(shortcut) == true);
        if (group is not null) return (group, null);
        foreach (var pair in _volumeShortcuts)
            if (pair.Key != targetCommand && pair.Value?.IsSameCombination(shortcut) == true)
                return (null, pair.Key);
        return null;
    }

    private static string OwnerName(GroupItem? group, VolumeCommand? command) =>
        group is not null ? group.Name : command == VolumeCommand.Mute ? "Mute/Unmute Selected Group" : $"Volume {command}";

    public GroupShortcut? GetVolumeShortcut(VolumeCommand command) => _volumeShortcuts[command];
    public string? GetVolumeError(VolumeCommand command) => _volumeErrors.GetValueOrDefault(command);
    public string GetVolumeLabel(VolumeCommand command)
    {
        var shortcut = GetVolumeShortcut(command);
        if (shortcut is null) return "Not assigned";
        var active = _registrations.Values.Any(item => item.Command == command && _registeredIds.Contains(item.Id));
        return shortcut.Label + (active ? "" : " (unavailable)");
    }

    public bool Restore(GroupItem group, out string? error)
    {
        _knownGroups.Add(group);
        if (group.Shortcut is null) { error = null; return true; }
        var success = TryAssign(group, group.Shortcut, out error);
        if (!success) group.SetShortcutStatus(false, error);
        return success;
    }

    public bool RestoreVolumeCommand(VolumeCommand command, GroupShortcut? shortcut, out string? error)
    {
        VerifyThread();
        // Retain unavailable saved configurations; never silently erase them.
        _volumeShortcuts[command] = shortcut;
        var success = TryAssignVolumeCommand(command, shortcut, out error);
        if (!success) _volumeErrors[command] = error;
        StatusChanged?.Invoke();
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
        return TryAssignCore(group, null, shortcut, allowReassign, out error);
    }

    public bool TryAssignVolumeCommand(VolumeCommand command, GroupShortcut? shortcut, out string? error,
        bool allowReassign = false) => TryAssignCore(null, command, shortcut, allowReassign, out error);

    private bool TryAssignCore(GroupItem? group, VolumeCommand? command, GroupShortcut? shortcut,
        bool allowReassign, out string? error)
    {
        VerifyThread();
        error = null;
        if (group is null && command is not (VolumeCommand.Up or VolumeCommand.Down or VolumeCommand.Mute))
        {
            error = "Unknown volume command.";
            return false;
        }
        var previous = _registrations.Values.FirstOrDefault(item =>
            ReferenceEquals(item.Group, group) && item.Command == command);
        if (shortcut is null && previous is null)
        {
            Commit(group, command, null);
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
        var owner = shortcut is null ? null : FindConflict(group, command, shortcut);
        if (owner is not null && !allowReassign)
        {
            error = $"{shortcut!.Label} is already assigned to '{OwnerName(owner.Value.Group, owner.Value.Command)}'.";
            return false;
        }
        if (previous is not null && shortcut is not null && previous.Shortcut.IsSameCombination(shortcut) &&
            _registeredIds.Contains(previous.Id) && owner is null)
        {
            _registrations[previous.Id] = previous with { Shortcut = shortcut };
            Commit(group, command, shortcut);
            return true;
        }
        var incoming = owner is null ? null : _registrations.Values.FirstOrDefault(item =>
            ReferenceEquals(item.Group, owner.Value.Group) && item.Command == owner.Value.Command);
        var incomingWasActive = incoming is not null && _registeredIds.Contains(incoming.Id);
        var newId = 0;
        if (shortcut is not null)
        {
            if (_nextId > 0xBFFF)
            {
                error = "This session has exhausted shortcut registration IDs. Restart Disturn.";
                return false;
            }
            // Transfers may cross between pass-through group observation and native volume hotkeys.
            if (incomingWasActive)
            {
                if (!ReleaseShortcut(incoming!.Id))
                {
                    error = ErrorMessage("Could not release the shortcut for reassignment", Marshal.GetLastWin32Error());
                    return false;
                }
                _registeredIds.Remove(incoming!.Id);
            }
            newId = _nextId++;
            if (!ActivateShortcut(newId, group, shortcut, command))
            {
                error = ErrorMessage(group is null
                    ? $"Could not register {shortcut.Label}; another app or Windows may already use it"
                    : $"Could not observe {shortcut.Label}",
                    Marshal.GetLastWin32Error());
                RestoreIncoming(incoming, incomingWasActive, ref error);
                return false;
            }
            _registeredIds.Add(newId);
        }
        if (previous is not null && _registeredIds.Contains(previous.Id) &&
            !ReleaseShortcut(previous.Id))
        {
            error = ErrorMessage("Could not release the previous shortcut; assignments were preserved", Marshal.GetLastWin32Error());
            if (newId != 0)
            {
                if (ReleaseShortcut(newId)) _registeredIds.Remove(newId);
                else error += " Replacement cleanup failed; it is inactive in Disturn and will be retried on Exit.";
            }
            RestoreIncoming(incoming, incomingWasActive, ref error);
            return false;
        }
        if (previous is not null)
        {
            _registeredIds.Remove(previous.Id);
            _registrations.Remove(previous.Id);
        }
        if (incoming is not null) _registrations.Remove(incoming.Id);
        if (shortcut is not null) _registrations[newId] = new Registration(newId, group, shortcut, command);
        if (owner is not null) Commit(owner.Value.Group, owner.Value.Command, null);
        Commit(group, command, shortcut);
        return true;
    }

    private void RestoreIncoming(Registration? incoming, bool wasActive, ref string? error)
    {
        if (!wasActive || incoming is null) return;
        if (ActivateShortcut(incoming.Id, incoming.Group, incoming.Shortcut, incoming.Command))
            _registeredIds.Add(incoming.Id);
        else
        {
            var warning = ErrorMessage("Could not roll back the original registration; its configuration was preserved",
                Marshal.GetLastWin32Error());
            incoming.Group?.SetShortcutStatus(false, warning);
            if (incoming.Command is VolumeCommand command) _volumeErrors[command] = warning;
            error += " " + warning;
        }
        StatusChanged?.Invoke();
    }

    private void Commit(GroupItem? group, VolumeCommand? command, GroupShortcut? shortcut)
    {
        if (group is not null)
        {
            group.Shortcut = shortcut;
            group.SetShortcutStatus(shortcut is not null, null);
        }
        else if (command is VolumeCommand volumeCommand)
        {
            _volumeShortcuts[volumeCommand] = shortcut;
            _volumeErrors[volumeCommand] = null;
        }
        StatusChanged?.Invoke();
    }
    private sealed record Registration(int Id, GroupItem? Group, GroupShortcut Shortcut, VolumeCommand? Command = null);

    private bool ActivateShortcut(int id, GroupItem? group, GroupShortcut shortcut, VolumeCommand? command)
    {
        if (group is null && command != VolumeCommand.Mute)
            return RegisterHotKey(_windowHandle, id, shortcut.Modifiers, shortcut.VirtualKey);
        if (!_groupObserver.TryStart(out var error))
        {
            Marshal.SetLastPInvokeError(error);
            return false;
        }
        _observedShortcuts[id] = (shortcut, command == VolumeCommand.Mute);
        PublishObservedShortcuts();
        return true;
    }

    private bool ReleaseShortcut(int id)
    {
        if (!_observedShortcuts.Remove(id)) return UnregisterHotKey(_windowHandle, id);
        PublishObservedShortcuts();
        return true;
    }

    private void PublishObservedShortcuts()
    {
        // Every change invalidates queued commands from the previous snapshot.
        _groupObserver.Update(_observedShortcuts.Where(pair => !pair.Value.Suppress || _hasSelectedGroup).Select(pair =>
            new GroupHotkeyObserver.Binding(pair.Key, pair.Value.Shortcut.VirtualKey,
                pair.Value.Shortcut.Modifiers, pair.Value.Suppress)).ToArray(),
            ++_observerVersion);
    }

    public void SetSelectedGroupAvailable(bool available)
    {
        VerifyThread();
        if (_hasSelectedGroup == available) return;
        _hasSelectedGroup = available;
        PublishObservedShortcuts();
    }

    private void ReportObserverFailure(string warning)
    {
        if (_disposed) return;
        foreach (var id in _observedShortcuts.Keys)
        {
            _registeredIds.Remove(id);
            if (_registrations.TryGetValue(id, out var registration))
            {
                registration.Group?.SetShortcutStatus(false, warning);
                if (registration.Command is VolumeCommand command) _volumeErrors[command] = warning;
            }
        }
        _observedShortcuts.Clear();
        PublishObservedShortcuts();
        _reportWarning(warning);
        StatusChanged?.Invoke();
    }

    private bool QueueObservedShortcut(int id, int version)
    {
        // Hook thread: queue only, never access models, sessions, or storage here.
        return _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || _suspendedForRecording || version != _observerVersion ||
                !_registeredIds.Contains(id) || !_registrations.TryGetValue(id, out var registration)) return;
            try
            {
                if (registration.Group is not null) _selectGroup(registration.Group);
                else if (registration.Command == VolumeCommand.Mute && _hasSelectedGroup) _toggleMute();
            }
            catch (Exception)
            {
                // Never log the triggering key or its configured combination.
                _reportWarning("The group shortcut command could not be completed.");
            }
        });
    }

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
            if (!ReleaseShortcut(registration.Id))
            {
                error = ErrorMessage("Could not pause shortcuts for recording", Marshal.GetLastWin32Error());
                TryResumeAfterRecording(out var restoreError);
                if (restoreError is not null) error += " " + restoreError;
                return false;
            }
            _registeredIds.Remove(registration.Id);
            registration.Group?.SetShortcutStatus(false, "Paused while recording.");
        }
        StatusChanged?.Invoke();
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
            if (ActivateShortcut(registration.Id, registration.Group, registration.Shortcut, registration.Command))
            {
                _registeredIds.Add(registration.Id);
                registration.Group?.SetShortcutStatus(true, null);
                if (registration.Command is VolumeCommand command) _volumeErrors[command] = null;
            }
            else
            {
                var warning = ErrorMessage($"Could not restore {registration.Shortcut.Label}; it is currently inactive",
                    Marshal.GetLastWin32Error());
                registration.Group?.SetShortcutStatus(false, warning);
                if (registration.Command is VolumeCommand command) _volumeErrors[command] = warning;
                warnings.Add(warning);
            }
        }
        _suspendedForRecording = false;
        error = warnings.Count == 0 ? null : string.Join(" ", warnings);
        StatusChanged?.Invoke();
        return error is null;
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmNcDestroy)
        {
            _disposed = true;
            _groupObserver.Dispose();
            _observedShortcuts.Clear();
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
                try
                {
                    if (registration.Command is VolumeCommand.Up or VolumeCommand.Down && registration.Command is VolumeCommand command)
                        _adjustVolume(command == VolumeCommand.Up ? 1 : -1);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Global shortcut command failed: {ex}");
                    _reportWarning("The global shortcut command could not be completed.");
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
            if (!ReleaseShortcut(id))
                warnings.Add(ErrorMessage("Could not release a global shortcut", Marshal.GetLastWin32Error()));
        }
        _groupObserver.Dispose();
        _observedShortcuts.Clear();
        _registeredIds.Clear();
        _registrations.Clear();
        foreach (var group in _knownGroups) group.SetShortcutStatus(false, "Disturn is exiting.");
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
