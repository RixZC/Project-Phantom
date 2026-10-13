using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Project_Phantom;

internal static class AudioSessionService
{
    public static IReadOnlyList<ApplicationEntry> GetActiveSessions()
    {
        var entries = new List<ApplicationEntry>();
        var seenSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var windowTitles = GetWindowTitlesByProcessId();

        using var deviceEnumerator = new MMDeviceEnumerator();
        var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var device in devices)
        {
            using (device)
            {
                try
                {
                    var deviceId = device.ID;
                    var deviceName = device.FriendlyName;
                    var sessions = device.AudioSessionManager.Sessions;

                    for (var index = 0; index < sessions.Count; index++)
                    {
                        AudioSessionControl? session = null;
                        try
                        {
                            session = sessions[index];
                            if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession)
                            {
                                continue;
                            }

                            string sessionInstanceId;
                            try
                            {
                                sessionInstanceId = session.GetSessionInstanceIdentifier;
                            }
                            catch (Exception ex) when (ex is COMException or InvalidOperationException)
                            {
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(sessionInstanceId))
                            {
                                continue;
                            }

                            uint processId;
                            try
                            {
                                processId = session.GetProcessID;
                            }
                            catch (Exception ex) when (ex is COMException or InvalidOperationException)
                            {
                                processId = 0;
                            }

                            var processName = GetProcessName(processId);
                            var windowTitle = windowTitles.TryGetValue(processId, out var title)
                                ? title
                                : "";
                            var sessionDisplayName = GetSessionDisplayName(session);
                            var displayName = FirstNonEmpty(windowTitle, sessionDisplayName, processName);

                            var entry = new ApplicationEntry(
                                displayName,
                                processName,
                                processId <= int.MaxValue ? (int)processId : 0,
                                deviceName,
                                deviceId,
                                sessionInstanceId);

                            if (seenSessions.Add(entry.SessionKey))
                            {
                                entries.Add(entry);
                            }
                        }
                        catch (Exception ex) when (ex is COMException or InvalidOperationException)
                        {
                            // A session can disappear while the list is being read.
                        }
                        finally
                        {
                            session?.Dispose();
                        }
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // Skip an output device that becomes unavailable during enumeration.
                }
            }
        }

        AddSessionSuffixes(entries);
        return entries
            .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ApplicationEntry> GetRunningProcesses()
    {
        var entries = new List<ApplicationEntry>();
        var windowTitles = GetWindowTitlesByProcessId();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    var processName = $"{process.ProcessName}.exe";
                    var title = windowTitles.TryGetValue((uint)processId, out var windowTitle)
                        ? windowTitle
                        : "";
                    var displayName = FirstNonEmpty(title, process.ProcessName);
                    entries.Add(new ApplicationEntry(displayName, processName, processId));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // A process may exit or become inaccessible during enumeration.
                }
            }
        }

        return entries
            .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string GetSessionDisplayName(AudioSessionControl session)
    {
        try
        {
            return session.DisplayName?.Trim() ?? "";
        }
        catch (COMException)
        {
            return "";
        }
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue)
        {
            return "Unknown process";
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return $"{process.ProcessName}.exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return $"Unknown process (PID {processId})";
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Unknown application";
    }

    private static void AddSessionSuffixes(List<ApplicationEntry> entries)
    {
        var duplicateGroups = entries
            .GroupBy(entry => $"{entry.DisplayName}|{entry.ProcessName}|{entry.DeviceName}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in duplicateGroups)
        {
            var number = 1;
            foreach (var entry in group)
            {
                entry.SessionSuffix = $"Session {number++}";
            }
        }
    }

    private static Dictionary<uint, string> GetWindowTitlesByProcessId()
    {
        var titles = new Dictionary<uint, string>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            var title = new StringBuilder(512);
            if (GetWindowText(window, title, title.Capacity) > 0)
            {
                GetWindowThreadProcessId(window, out var processId);
                if (processId != 0 && !titles.ContainsKey(processId))
                {
                    titles.Add(processId, title.ToString().Trim());
                }
            }

            return true;
        }, IntPtr.Zero);

        return titles;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
