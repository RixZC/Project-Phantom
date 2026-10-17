# Group mute

Mute is independent of VolumePercent. Session discovery continues to cover all
active render endpoints and all matching sessions. The controller uses NAudio's
ISimpleAudioVolume.SetMute wrapper, never endpoint/master mute. Disturn's own
sessions are excluded from group control so feedback remains independent.

A session already muted when the group is muted stays muted on group unmute.
Only a mute introduced by the current controller is released. Observed external
unmutes relinquish ownership; reconciliation never repeatedly forces mute or
reapplies volume on existing sessions. Cleanup releases owned mutes, including
inactive sessions, when possible. No scalar baseline is captured or restored.

Core Audio has one shared session-mute bit, not separate application/group mute
layers. A request to mute an already-muted session produces no notification.
An external unmute/re-mute entirely between observations cannot be distinguished
from the current owned mute. After a crash or unavailable device, mute ownership
cannot be recovered safely: existing live mutes are preserved on reopening,
which can require manually unmuting that session in Windows Volume Mixer.
Normal shutdown releases owned mutes before the next launch reapplies saved
muted groups. Feedback never plays merely because a saved group was loaded.

## Shortcuts

Group-selection bindings remain pass-through; Volume Up/Down retain RegisterHotKey.
Mute uses the existing dedicated-thread keyboard hook with a consuming binding
only while a valid group is selected. It consumes only a successfully queued
matching mute press and that press's repeats/key-up. Recording pauses bindings.
No selection means no Phantom mute action; Windows receives media mute normally.
The hook dispatches to the UI thread and never performs audio/storage work itself.
There remains a small asynchronous boundary: shutdown, recording, or reassignment
can invalidate a queued command after its input was accepted. No keystrokes are
logged, retained as history, or transmitted.

VK_VOLUME_MUTE is the first-use default. Explicitly clearing it stays unbound.
The existing recorder supports custom modifiers and conflict reassignment. The
mute dialog offers Reset to media mute; Save still validates/reassigns it.
A hook observes keyboard media events, not hardware-direct mute, arbitrary HID
reports, or WM_APPCOMMAND-only devices. Secure desktops, elevated/protected apps,
other hooks, and anti-cheat software can limit behavior. Windows can silently
remove a timed-out hook; the callback remains deliberately lightweight.

## Persistence and sound

Schema 7 adds group IsMuted, Settings.MuteShortcut, and Settings.MuteFeedbackSounds.
Old groups default to unmuted; feedback defaults on. Missing MuteShortcut selects
the first-use default; JSON null remains explicitly unbound. Existing pre-upgrade
backup, malformed-file protection, atomic replacement, and legacy paths remain.

MuteFeedbackService decodes Assets/Mute.wav or Assets/Unmute.wav on a background
task and plays through NAudio WASAPI in the calling process session. The assets
are copied to build/publish output. VolumeSampleProvider scales decoded samples
by Settings.FeedbackVolumePercent / 100: zero is silent, 100 is unamplified.
This additive setting defaults to 35 for older saves and is clamped to 0-100.
It is retained when feedback is disabled; the slider uses native disabled styling.
No endpoint or session-volume setter is used for feedback gain. Playback respects
Windows master mute/volume and fails silently without undoing mute. Disabling
feedback cancels pending/current playback. A newer feedback request cancels the
previous clip. Each request captures the current gain, so changing the slider
affects subsequent sounds without starting a preview sound or changing groups.
Selecting groups, startup restore, volume changes, and reconciliation do not play
feedback. Header selection is a one-way circle indicator bound to IsSelected;
its existing SelectGroup handler remains authoritative. Speaker glyph buttons
change only IsMuted, with native hover/pressed styling and accessible state labels.

## Checks

From the repository root:

```powershell
dotnet build Disturn.slnx -c Debug -p:Platform=x64
dotnet run --project tests/Disturn.DeploymentChecks -- Disturn/Package.appxmanifest
dotnet run --project tests/Disturn.MuteAudioChecks -- --keyboard --feedback
dotnet publish Disturn/Disturn.csproj -c Release -r win-x64 -p:Platform=x64 -p:PublishProfile=Unpackaged-win-x64
dotnet run --project tests/Disturn.TrayIconChecks -- artifacts/publish/Disturn-win-x64
```

Deployment checks use isolated JSON/registry fixtures. MuteAudioChecks creates a
silent session owned by its test process; it never edits another application's
session or saved groups. Optional --keyboard emits synthetic media-mute events
with a consuming test binding installed and checks master volume/mute remain
unchanged. Optional --feedback starts both supplied WAV clips through the test process;
audibility and perceived clip correctness remain manual. These checks are not
a physical-keyboard/knob or MainWindow integration test.

Manual acceptance:

1. Assign a playing app, set a nonzero volume, and mute/unmute using the group
   button. Confirm the percentage stays fixed and selection does not change.
2. Independently mute one app in Windows Mixer; group mute/unmute must preserve it.
3. Test multiple sessions, other render devices, and an app starting audio later.
4. Restart normally with the group muted; verify stored mute state is reapplied.
5. With the app hidden, test the physical media mute key with a selected group;
   verify only that group mutes. With no group, Windows handles mute normally.
6. Record, clear, reset, conflict/reassign, and restart with a custom mute binding.
7. Verify both feedback clips audibly, the feedback toggle, and the installed
   Assets paths. Confirm the tray tooltip shows Muted without changing its number.
8. Check existing group selection keys, volume commands, auto-return, tray restore,
   and normal exit. Test game/elevation/hardware-specific limitations separately.
