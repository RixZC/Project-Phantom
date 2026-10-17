using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Disturn;

/// <summary>Scales feedback samples in Disturn's own WASAPI session, never endpoint/session volume.</summary>
internal sealed class MuteFeedbackService : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private bool _enabled = true;
    private bool _disposed;

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            if (!enabled) _current?.Cancel();
        }
    }

    public void Play(bool muted, int volumePercent)
    {
        CancellationTokenSource request;
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            _current?.Cancel();
            if (volumePercent <= 0) return; // Silence also stops any previous clip.
            _current = request = new CancellationTokenSource();
        }
        // Decode, open the endpoint, wait, and dispose off the UI thread.
        _ = Task.Run(() => PlayFile(muted, volumePercent, request));
    }

    internal static ISampleProvider CreateSamples(WaveFileReader reader, int volumePercent) =>
        new VolumeSampleProvider(reader.ToSampleProvider())
        {
            Volume = Math.Clamp(volumePercent, 0, 100) / 100f
        };

    private void PlayFile(bool muted, int volumePercent, CancellationTokenSource request)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", muted ? "Mute.wav" : "Unmute.wav");
            if (request.IsCancellationRequested || !File.Exists(path)) return;
            using var completed = new ManualResetEventSlim();
            using var reader = new WaveFileReader(path);
            using var player = new WasapiOut(AudioClientShareMode.Shared, true, 100);
            player.PlaybackStopped += (_, _) => completed.Set();
            using var cancellation = request.Token.Register(completed.Set);
            player.Init(CreateSamples(reader, volumePercent).ToWaveProvider());
            if (request.IsCancellationRequested) return;
            player.Play();
            completed.Wait();
            player.Stop();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Missing/invalid WAVs and unavailable devices never interrupt group mute.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, request)) _current = null;
                request.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current?.Cancel();
        }
    }
}
