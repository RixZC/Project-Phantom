using System;

namespace Project_Phantom;

public sealed class ApplicationEntry
{
    public string DisplayName { get; }
    public string ProcessName { get; }
    public string? ExecutablePath { get; }
    public int ProcessId { get; }
    public string DeviceName { get; }
    public string DeviceId { get; }
    public string SessionInstanceId { get; }
    public string SessionSuffix { get; internal set; } = "";
    public bool HasAudioSession { get; }

    public string Details => HasAudioSession
        ? string.IsNullOrEmpty(SessionSuffix)
            ? $"{ProcessName} • {DeviceName}"
            : $"{ProcessName} • {DeviceName} • {SessionSuffix}"
        : ProcessName;

    public string SessionKey => HasAudioSession
        ? $"session:{DeviceId}|{SessionInstanceId}"
        : $"process:{ProcessId}";

    public bool IsSameApplication(ApplicationEntry other)
    {
        if (!string.IsNullOrWhiteSpace(ExecutablePath) && !string.IsNullOrWhiteSpace(other.ExecutablePath))
        {
            return string.Equals(
                ExecutablePath.Trim().Replace('/', '\\'),
                other.ExecutablePath.Trim().Replace('/', '\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    public ApplicationEntry(
        string displayName,
        string processName,
        int processId,
        string deviceName,
        string deviceId,
        string sessionInstanceId,
        string? executablePath = null)
    {
        DisplayName = displayName;
        ProcessName = processName;
        ExecutablePath = executablePath;
        ProcessId = processId;
        DeviceName = deviceName;
        DeviceId = deviceId;
        SessionInstanceId = sessionInstanceId;
        HasAudioSession = true;
    }

    public ApplicationEntry(string displayName, string processName, int processId, string? executablePath = null)
    {
        DisplayName = displayName;
        ProcessName = processName;
        ExecutablePath = executablePath;
        ProcessId = processId;
        DeviceName = "";
        DeviceId = "";
        SessionInstanceId = "";
        HasAudioSession = false;
    }
}
