using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundpad.Audio;

/// <summary>
/// Real <see cref="IMicCapture"/> backed by <see cref="WasapiCapture"/>.
/// Pushes the device's native PCM into a producer/consumer buffer, exposes
/// it as an <see cref="ISampleProvider"/>, and resamples to the working
/// format the engine asks for.
/// </summary>
public class WasapiMicCapture : IMicCapture
{
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider _silence = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
    private ISampleProvider _output;
    private WaveFormat _workingFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private bool _disposed;

    public WasapiMicCapture()
    {
        _output = _silence;
    }

    public WaveFormat OutputFormat => _workingFormat;
    public ISampleProvider Samples => new PassThroughProvider(this);
    public bool IsRunning => _capture is not null;

    public event Action<Exception>? CaptureError;

    public void Start(string? deviceId, WaveFormat workingFormat)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WasapiMicCapture));

        lock (_lock)
        {
            StopLocked();
            _workingFormat = workingFormat;
            _silence = new SilenceProvider(workingFormat);
            _output = _silence;

            MMDevice device;
            using (var en = new MMDeviceEnumerator())
            {
                if (string.IsNullOrEmpty(deviceId))
                {
                    device = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                }
                else
                {
                    // Look up by WASAPI endpoint id (stable across renames/replugs).
                    device = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                        .FirstOrDefault(d => string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Mic device not found by id: {deviceId}");
                }
            }

            var capture = new WasapiCapture(device);
            // Buffer: native device format. Sized at ~1s worth of audio so
            // hiccups don't drop samples. Discard on overflow — we want low
            // latency far more than perfect tail audio.
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
                ReadFully = true, // emit silence when buffer is empty so the mixer never blocks
            };

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0) buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) CaptureError?.Invoke(e.Exception);
            };

            // Build the resample chain to the working format.
            ISampleProvider chain = buffer.ToSampleProvider();
            if (chain.WaveFormat.SampleRate != workingFormat.SampleRate)
            {
                chain = new WdlResamplingSampleProvider(chain, workingFormat.SampleRate);
            }
            if (chain.WaveFormat.Channels != workingFormat.Channels)
            {
                chain = workingFormat.Channels switch
                {
                    1 => new StereoToMonoSampleProvider(EnsureStereo(chain)),
                    2 => EnsureStereo(chain),
                    _ => chain,
                };
            }

            try
            {
                capture.StartRecording();
            }
            catch
            {
                capture.Dispose();
                throw;
            }

            _capture = capture;
            _buffer = buffer;
            _output = chain;
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        if (_capture is not null)
        {
            try { _capture.StopRecording(); } catch { /* ignore */ }
            try { _capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
        }
        _buffer = null;
        _output = _silence;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static ISampleProvider EnsureStereo(ISampleProvider mono) =>
        mono.WaveFormat.Channels == 1 ? new MonoToStereoSampleProvider(mono) : mono;

    /// <summary>
    /// Trampolines reads into whatever the current resample chain is. Lets us
    /// swap chains during Start/Stop without re-creating the provider the
    /// mixer holds a reference to.
    /// </summary>
    private sealed class PassThroughProvider : ISampleProvider
    {
        private readonly WasapiMicCapture _owner;
        public PassThroughProvider(WasapiMicCapture owner) { _owner = owner; }
        public WaveFormat WaveFormat => _owner._workingFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            ISampleProvider chain;
            lock (_owner._lock) chain = _owner._output;
            return chain.Read(buffer, offset, count);
        }
    }

    /// <summary>
    /// Reads silence at the working format. Used when no device is started.
    /// </summary>
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
