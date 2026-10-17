using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Disturn;

/// <summary>Tracks live audio sessions assigned to groups and applies their direct volume targets.</summary>
internal sealed class GroupVolumeController : IDisposable
{
    private readonly IReadOnlyList<GroupItem> _groups;
    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public GroupVolumeController(IReadOnlyList<GroupItem> groups, DispatcherQueue dispatcher)
    {
        _groups = groups;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Reconcile();
    }

    public void Start()
    {
        Reconcile();
        _timer.Start();
    }

    public void ApplyGroup(GroupItem group)
    {
        foreach (var pair in _sessions.Where(pair => ReferenceEquals(pair.Value.Group, group)).ToArray())
        {
            ApplyTarget(pair.Key, pair.Value, group);
        }
    }

    public bool ApplyMuteGroup(GroupItem group)
    {
        var success = true;
        foreach (var pair in _sessions.Where(pair => ReferenceEquals(pair.Value.Group, group)).ToArray())
        {
            if (pair.Value.Control.TryApplyGroupMute(group.IsMuted)) continue;
            Release(pair.Key, pair.Value);
            success = false;
        }
        return success;
    }

    public void Reconcile()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<ApplicationEntry> active;
        try
        {
            active = AudioSessionService.GetActiveSessions();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in active)
        {
            var group = FindGroup(session);
            if (group is null)
            {
                continue;
            }

            seen.Add(session.SessionKey);
            if (_sessions.TryGetValue(session.SessionKey, out var existing))
            {
                if (!ReferenceEquals(existing.Group, group))
                {
                    Release(session.SessionKey, existing);
                    OpenAndTrack(session, group);
                }
                else if (!existing.Control.TryApplyGroupMute(group.IsMuted))
                {
                    Release(session.SessionKey, existing);
                }
                continue;
            }

            OpenAndTrack(session, group);
        }

        foreach (var stale in _sessions.Where(pair => !seen.Contains(pair.Key)).ToArray())
        {
            Release(stale.Key, stale.Value);
        }
    }

    private GroupItem? FindGroup(ApplicationEntry session)
    {
        // Feedback belongs to Disturn itself, even if someone assigns Disturn to a group.
        if (session.ProcessId == Environment.ProcessId ||
            (!string.IsNullOrEmpty(Environment.ProcessPath) &&
             string.Equals(session.ExecutablePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)))
            return null;
        foreach (var group in _groups)
        {
            if (group.Applications.Any(application => application.IsSameApplication(session)))
            {
                return group;
            }
        }

        return null;
    }

    private void OpenAndTrack(ApplicationEntry entry, GroupItem group)
    {
        var control = AudioSessionService.TryOpenActiveSession(entry);
        if (control is null)
        {
            return;
        }

        var tracked = new TrackedSession(group, control);
        _sessions[entry.SessionKey] = tracked;
        ApplyTarget(entry.SessionKey, tracked, group);
        if (_sessions.ContainsKey(entry.SessionKey) && !control.TryApplyGroupMute(group.IsMuted))
            Release(entry.SessionKey, tracked);
    }

    private void ApplyTarget(string key, TrackedSession tracked, GroupItem group)
    {
        var target = Math.Clamp(group.VolumePercent / 100f, 0f, 1f);
        if (!tracked.Control.TrySetVolume(target))
        {
            // Remove failed handles so the next reconciliation can reopen the session.
            Release(key, tracked);
        }
    }

    private void Release(string key, TrackedSession tracked)
    {
        _sessions.Remove(key);
        tracked.Control.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        foreach (var pair in _sessions.ToArray())
        {
            Release(pair.Key, pair.Value);
        }
    }

    private sealed class TrackedSession
    {
        public GroupItem Group { get; }
        public AudioSessionVolumeControl Control { get; }

        public TrackedSession(GroupItem group, AudioSessionVolumeControl control)
        {
            Group = group;
            Control = control;
        }
    }
}
