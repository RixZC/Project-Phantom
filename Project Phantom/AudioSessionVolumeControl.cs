using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Runtime.InteropServices;

namespace Project_Phantom;

/// <summary>
/// A short-lived handle to one currently active Windows audio session.
/// The caller must dispose it when that session is no longer selected.
/// </summary>
internal sealed class AudioSessionVolumeControl : IDisposable
{
    private AudioSessionControl? _session;
    private SimpleAudioVolume? _volume;

    public AudioSessionVolumeControl(AudioSessionControl session, SimpleAudioVolume volume)
    {
        _session = session;
        _volume = volume;
    }

    public bool TryGetVolume(out float volume)
    {
        try
        {
            if (_session?.State != AudioSessionState.AudioSessionStateActive || _volume is null)
            {
                volume = 0;
                Dispose();
                return false;
            }

            volume = _volume.Volume;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            volume = 0;
            Dispose();
            return false;
        }
    }

    public bool TrySetVolume(float volume)
    {
        try
        {
            if (_session?.State != AudioSessionState.AudioSessionStateActive || _volume is null)
            {
                Dispose();
                return false;
            }

            _volume.Volume = Math.Clamp(volume, 0f, 1f);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        _volume?.Dispose();
        _volume = null;
        _session?.Dispose();
        _session = null;
    }
}
