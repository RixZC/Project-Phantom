using Disturn;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

// Silent, isolated test sessions only. Never change another application's audio.
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    checks++;
    Console.WriteLine("PASS: " + label);
}
using var enumerator = new MMDeviceEnumerator();
using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
var masterVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
var masterMute = endpoint.AudioEndpointVolume.Mute;
var buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2)) { ReadFully = true };
using var player = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 100);
player.Init(buffer); // ReadFully emits silence indefinitely, with no audible test tone.
player.Play();
Thread.Sleep(300);
var entry = AudioSessionService.GetActiveSessions().FirstOrDefault(item => item.ProcessId == Environment.ProcessId && item.DeviceId == endpoint.ID)
    ?? throw new InvalidOperationException("Could not discover the isolated silent test session.");
Check(entry.HasAudioSession, "active session discovery finds the new test session");
var sessions = endpoint.AudioSessionManager.Sessions;
using var reader = Enumerable.Range(0, sessions.Count).Select(index => sessions[index])
    .First(item => item.GetProcessID == (uint)Environment.ProcessId);
var volume = reader.SimpleAudioVolume;
var originalVolume = volume.Volume;
var originalMute = volume.Mute;
try
{
    volume.Volume = 0.64f;
    volume.Mute = false;
    using (var control = AudioSessionService.TryOpenActiveSession(entry)
        ?? throw new InvalidOperationException("Could not reopen the isolated test session."))
    {
        Check(control.TryApplyGroupMute(true) && volume.Mute && Math.Abs(volume.Volume - 0.64f) < 0.0001f,
            "Core Audio group mute sets mute without changing the 64% session scalar");
        Check(control.TryApplyGroupMute(false) && !volume.Mute && Math.Abs(volume.Volume - 0.64f) < 0.0001f,
            "Core Audio group unmute preserves the 64% session scalar");
        volume.Mute = true;
        Check(control.TryApplyGroupMute(true) && control.TryApplyGroupMute(false) && volume.Mute,
            "an independently muted session stays muted");
        volume.Mute = false;
        Check(control.TryApplyGroupMute(true) && volume.Mute, "a new mute cycle owns its session mute");
    }
    Check(!volume.Mute, "disposing the control releases its owned mute");
    Check(endpoint.AudioEndpointVolume.Mute == masterMute &&
        Math.Abs(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar - masterVolume) < 0.0001f,
        "session operations leave Windows master volume and master mute untouched");
}
finally
{
    volume.Mute = originalMute;
    volume.Volume = originalVolume;
    player.Stop();
}

// Inspect actual decoded samples for both supplied files, without playing them.
float[] ReadFeedback(string path, int? gain)
{
    using var wav = new WaveFileReader(path);
    var samples = gain is int percent ? MuteFeedbackService.CreateSamples(wav, percent) : wav.ToSampleProvider();
    var values = new List<float>();
    var block = new float[4096];
    int read;
    while ((read = samples.Read(block, 0, block.Length)) > 0) values.AddRange(block.Take(read));
    return values.ToArray();
}
foreach (var file in new[] { "Mute.wav", "Unmute.wav" })
{
    var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
    var original = ReadFeedback(path, null);
    var zero = ReadFeedback(path, 0);
    var normal = ReadFeedback(path, 100);
    var quiet = ReadFeedback(path, 35);
    Check(original.Any(value => value != 0), file + " contains non-silent source samples");
    Check(zero.Length == original.Length && zero.All(value => value == 0), file + " is exactly silent at zero percent");
    Check(normal.SequenceEqual(original), file + " is unamplified at 100 percent");
    Check(quiet.Length == original.Length && quiet.Zip(original).All(pair => Math.Abs(pair.First - pair.Second * 0.35f) < 0.0000001f),
        file + " samples scale by 0.35 at 35 percent");
}
using (var disabledFeedback = new MuteFeedbackService())
{
    disabledFeedback.SetEnabled(false);
    disabledFeedback.Play(true, 100);
    Check(typeof(MuteFeedbackService).GetField("_current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(disabledFeedback) is null, "disabled feedback does not queue playback");
    disabledFeedback.SetEnabled(true);
    disabledFeedback.Play(false, 0);
    Check(typeof(MuteFeedbackService).GetField("_current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(disabledFeedback) is null, "zero feedback volume does not queue playback");
}

if (args.Contains("--feedback"))
{
    // Actual playback-path check. Audibility/correct perceived sound remains manual.
    foreach (var muted in new[] { true, false })
    {
        using var feedback = new MuteFeedbackService();
        Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", muted ? "Mute.wav" : "Unmute.wav")),
            "feedback WAV exists at the runtime Assets path");
        feedback.Play(muted, 35);
        Thread.Sleep(300);
        Check(AudioSessionService.GetActiveSessions().Any(item => item.ProcessId == Environment.ProcessId),
            "feedback playback uses the test application's own active session");
    }
    Check(endpoint.AudioEndpointVolume.Mute == masterMute &&
        Math.Abs(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar - masterVolume) < 0.0001f,
        "feedback playback leaves Windows master state unchanged");
}

if (args.Contains("--keyboard"))
{
    // Only synthetic media mute is emitted, with a consuming binding installed.
    // This tests the production hook, not physical hardware or UI command routing.
    var observedDownstream = 0;
    var handled = 0;
    var passedGroupPresses = 0;
    var selectedGroupPresses = 0;
    using var downstream = new GroupHotkeyObserver((id, _) => { if (id == 1) Interlocked.Increment(ref observedDownstream); else Interlocked.Increment(ref passedGroupPresses); return true; },
        warning => Console.Error.WriteLine(warning));
    downstream.Update(new[] { new GroupHotkeyObserver.Binding(1, 0xAD, 0), new GroupHotkeyObserver.Binding(3, 0x87, 0) }, 1);
    Check(downstream.TryStart(out _), "downstream test observer installs");
    using var observer = new GroupHotkeyObserver((id, _) => { if (id == 2) Interlocked.Increment(ref handled); else Interlocked.Increment(ref selectedGroupPresses); return true; },
        warning => Console.Error.WriteLine(warning));
    observer.Update(new[] { new GroupHotkeyObserver.Binding(2, 0xAD, 0, Suppress: true), new GroupHotkeyObserver.Binding(4, 0x87, 0) }, 1);
    Check(observer.TryStart(out _), "consuming mute observer installs on its dedicated thread");
    for (var index = 0; index < 4; index++) Native.keybd_event(0xAD, 0, 0, UIntPtr.Zero);
    Native.keybd_event(0xAD, 0, 2, UIntPtr.Zero);
    Thread.Sleep(350);
    Check(Volatile.Read(ref handled) == 1, "synthetic media mute triggers once, ignoring held-key repeats");
    Check(Volatile.Read(ref observedDownstream) == 0, "handled media mute is suppressed before downstream observers");
    Check(endpoint.AudioEndpointVolume.Mute == masterMute &&
        Math.Abs(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar - masterVolume) < 0.0001f,
        "handled synthetic media mute leaves Windows master mute and volume unchanged");
    Native.keybd_event(0x87, 0, 0, UIntPtr.Zero); // F24: no Windows volume command.
    Native.keybd_event(0x87, 0, 2, UIntPtr.Zero);
    Thread.Sleep(150);
    Check(Volatile.Read(ref selectedGroupPresses) == 1 && Volatile.Read(ref passedGroupPresses) == 1,
        "group-selection binding still passes the same press downstream");
    observer.Update(Array.Empty<GroupHotkeyObserver.Binding>(), 2);
    Native.keybd_event(0x87, 0, 0, UIntPtr.Zero);
    Native.keybd_event(0x87, 0, 2, UIntPtr.Zero);
    Thread.Sleep(150);
    Check(Volatile.Read(ref selectedGroupPresses) == 1 && Volatile.Read(ref passedGroupPresses) == 2,
        "paused bindings do not run commands or consume unrelated input");
    // Empty bindings represent no selection/recording. Avoid emitting media mute
    // in that state: Windows should act normally, which would alter real master mute.
}
Console.WriteLine($"Completed {checks} live checks. No saved data or other applications' sessions were modified.");
static class Native
{
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
