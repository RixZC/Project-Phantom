using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Project_Phantom;

internal static class MasterVolumeService
{
    public static bool TryAdjust(int direction, int stepPercent, out string? error)
    {
        error = null;
        if (direction == 0) return true;
        try
        {
            // Resolve on each command to follow default-device changes and avoid
            // keeping a stale endpoint. Never change the endpoint's mute state.
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = endpoint.AudioEndpointVolume; // Owned and disposed by MMDevice.
            var currentPercent = volume.MasterVolumeLevelScalar * 100f;
            var targetPercent = Math.Clamp(currentPercent + Math.Sign(direction) * stepPercent, 0f, 100f);
            volume.MasterVolumeLevelScalar = targetPercent / 100f;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            error = "Could not adjust the default playback device's master volume: " + ex.Message;
            return false;
        }
    }
}
