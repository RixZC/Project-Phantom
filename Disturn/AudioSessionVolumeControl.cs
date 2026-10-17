using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System;
using System.Runtime.InteropServices;

namespace Disturn;

/// <summary>
/// A short-lived handle to one currently active Windows audio session.
/// The caller must dispose it when that session is no longer selected.
/// </summary>
internal sealed class AudioSessionVolumeControl : IDisposable
{
    private AudioSessionControl? _session;
    private SimpleAudioVolume? _volume;
    private readonly SessionMuteState _muteState = new();

    public AudioSessionVolumeControl(AudioSessionControl session, SimpleAudioVolume volume)
    {
        _session = session;
        _volume = volume;
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

    public bool TryApplyGroupMute(bool muted)
    {
        try
        {
            if (_session is null || _volume is null || _session.State == AudioSessionState.AudioSessionStateExpired)
                return false;
            return _muteState.Apply(muted, _volume.Mute, value =>
            {
                // NAudio calls ISimpleAudioVolume.SetMute; volume scalar is untouched.
                _volume.Mute = value;
                return true;
            });
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Includes inactive sessions: release only this instance's owned mute.
        // A process crash cannot restore ownership; on reopening, preserve live mutes.
        TryApplyGroupMute(false);
        _volume?.Dispose();
        _volume = null;
        _session?.Dispose();
        _session = null;
    }
}
