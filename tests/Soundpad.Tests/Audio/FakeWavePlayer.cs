using NAudio.Wave;

namespace Soundpad.Tests.Audio;

public class FakeWavePlayer : IWavePlayer
{
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public int ManagedThreadIdAtInit = -1;
    public int ManagedThreadIdAtPlay = -1;
    public int ManagedThreadIdAtDispose = -1;
    public string DeviceName;
    public bool Disposed;

    public FakeWavePlayer(string deviceName)
    {
        DeviceName = deviceName;
        ManagedThreadIdAtInit = Thread.CurrentThread.ManagedThreadId;
    }
    public void Init(IWaveProvider waveProvider) { OutputWaveFormat = waveProvider.WaveFormat; }
    public void Play() { ManagedThreadIdAtPlay = Thread.CurrentThread.ManagedThreadId; PlaybackState = PlaybackState.Playing; }
    public void Stop() { PlaybackState = PlaybackState.Stopped; }
    public void Pause() { PlaybackState = PlaybackState.Paused; }
    public void Dispose() { ManagedThreadIdAtDispose = Thread.CurrentThread.ManagedThreadId; Disposed = true; }
    public void RaiseStopped() => PlaybackStopped?.Invoke(this, new StoppedEventArgs());
}
