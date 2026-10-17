using System;

namespace Disturn;

/// <summary>Owns only mute changes made by this group, never an existing independent mute.</summary>
internal sealed class SessionMuteState
{
    private bool _groupWasMuted;
    private bool _ownsMute;

    public bool Apply(bool groupMuted, bool currentMuted, Func<bool, bool> trySetMute)
    {
        if (!groupMuted)
        {
            if (_ownsMute && currentMuted && !trySetMute(false)) return false;
            _ownsMute = false;
            _groupWasMuted = false;
            return true;
        }
        if (!_groupWasMuted)
        {
            if (!currentMuted && !trySetMute(true)) return false;
            _ownsMute = !currentMuted;
            _groupWasMuted = true;
        }
        else if (!currentMuted)
        {
            // Respect an external unmute observed while the group is muted.
            // Never periodically reassert mute or fight Windows Volume Mixer.
            _ownsMute = false;
        }
        return true;
    }
}
