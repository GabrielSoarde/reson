using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

/// <summary>
/// Test-only <see cref="IMicCapture"/> that produces deterministic samples
/// (silence by default, but can be swapped to a sine generator). Never
/// touches real Windows audio devices.
/// </summary>
public class FakeMicCapture : IMicCapture
{
    private WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private bool _running;
    private bool _disposed;
    public string? LastStartedDevice { get; private set; }
    public int StartCalls;
    public int StopCalls;

    public WaveFormat OutputFormat => _format;
    public ISampleProvider Samples { get; private set; } = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    public bool IsRunning => _running;
    public event Action<Exception>? CaptureError;

    /// <summary>Swap the samples provider to feed the engine a known signal.</summary>
    public void SetSamples(ISampleProvider provider) { Samples = provider; }

    /// <summary>Raise CaptureError for tests that exercise the error path.</summary>
    public void RaiseCaptureError(Exception ex) => CaptureError?.Invoke(ex);

    public void Start(string? deviceFriendlyName, WaveFormat workingFormat)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FakeMicCapture));
        StartCalls++;
        LastStartedDevice = deviceFriendlyName;
        _format = workingFormat;
        if (Samples is SilenceProvider) Samples = new SilenceProvider(workingFormat);
        _running = true;
    }

    public void Stop()
    {
        StopCalls++;
        _running = false;
    }

    public void Dispose()
    {
        _disposed = true;
        _running = false;
    }

    private sealed class SilenceProvider : ISampleProvider
    {
        public SilenceProvider(WaveFormat fmt) { WaveFormat = fmt; }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
