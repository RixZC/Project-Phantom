namespace Project_Phantom;

public sealed class ApplicationEntry
{
    public string DisplayName { get; }
    public string ProcessName { get; }
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
        : $"{ProcessName} • Running process (audio session may be unavailable)";

    public string SessionKey => HasAudioSession
        ? $"session:{DeviceId}|{SessionInstanceId}"
        : $"process:{ProcessId}";

    public ApplicationEntry(
        string displayName,
        string processName,
        int processId,
        string deviceName,
        string deviceId,
        string sessionInstanceId)
    {
        DisplayName = displayName;
        ProcessName = processName;
        ProcessId = processId;
        DeviceName = deviceName;
        DeviceId = deviceId;
        SessionInstanceId = sessionInstanceId;
        HasAudioSession = true;
    }

    public ApplicationEntry(string displayName, string processName, int processId)
    {
        DisplayName = displayName;
        ProcessName = processName;
        ProcessId = processId;
        DeviceName = "";
        DeviceId = "";
        SessionInstanceId = "";
        HasAudioSession = false;
    }
}
