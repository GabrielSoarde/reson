using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundpad.Audio;

/// <summary>
/// Wraps a <see cref="CachedSound"/> as an <see cref="ISampleProvider"/> that
/// reads through the cached PCM bytes. When the read position reaches the
/// end, the provider returns 0-length reads and raises <see cref="Finished"/>
/// exactly once. The mixer can then drop it. Has an adjustable Volume.
/// </summary>
/// <remarks>
/// <para>
/// The PCM bytes in <see cref="CachedSound"/> are always 32-bit IEEE float in
/// the sound's native sample rate/channel count, so reads are just memory
/// copies into the output buffer (with optional resampling/up-mix).
/// </para>
/// <para>
/// Two SoundSampleProviders built from the same CachedSound have independent
/// read positions — that's how we feed the same sound to both the game mixer
/// and the monitor mixer.
/// </para>
/// </remarks>
public sealed class SoundSampleProvider : ISampleProvider
{
    /// <summary>Linear fade-in length applied to the start of every sound.</summary>
    public const double FadeInMs = 10.0;
    /// <summary>Linear fade-out length applied at end-of-stream and on Stop.</summary>
    public const double FadeOutMs = 20.0;

    private readonly CachedSound _cached;
    private readonly ISampleProvider _chain;
    private readonly RawFloatStream _raw;
    private readonly VolumeSampleProvider _volume;
    private readonly int _samplesPerChannelMs;
    private readonly int _fadeInSamples;     // per-channel
    private readonly int _fadeOutSamples;    // per-channel
    private readonly int _channels;
    private long _samplesEmitted;            // per-channel frames read out
    private bool _finishedRaised;
    private bool _stopFadeActive;
    private long _stopFadeStartFrame;        // per-channel frame index at which Stop fade began

    public SoundSampleProvider(CachedSound cached, WaveFormat workingFormat, float initialVolume = 1.0f)
    {
        _cached = cached;
        _raw = new RawFloatStream(cached);

        ISampleProvider chain = _raw;
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
        _volume = new VolumeSampleProvider(chain) { Volume = initialVolume };
        _chain = _volume;
        WaveFormat = _chain.WaveFormat;
        _channels = WaveFormat.Channels;
        _samplesPerChannelMs = WaveFormat.SampleRate / 1000;
        _fadeInSamples = (int)(WaveFormat.SampleRate * FadeInMs / 1000.0);
        _fadeOutSamples = (int)(WaveFormat.SampleRate * FadeOutMs / 1000.0);
    }

    public WaveFormat WaveFormat { get; }

    public float Volume
    {
        get => _volume.Volume;
        set => _volume.Volume = value;
    }

    /// <summary>True once <see cref="Finished"/> has been raised.</summary>
    public bool IsFinished => _finishedRaised;

    /// <summary>True after <see cref="BeginFadeOut"/> has been called.</summary>
    public bool IsFadingOut => _stopFadeActive;

    /// <summary>
    /// Raised when the cached PCM is fully consumed. Always raised exactly
    /// once, on the thread that called <see cref="Read"/> at the moment EOF
    /// is reached.
    /// </summary>
    public event Action<SoundSampleProvider>? Finished;

    /// <summary>
    /// Begin a linear fade-out over the next <see cref="FadeOutMs"/> milliseconds.
    /// After the fade window completes, <see cref="Finished"/> is raised and
    /// subsequent reads return 0. Idempotent — repeat calls are no-ops.
    /// Intended use: caller wants to Stop the sound but avoid an audible click.
    /// </summary>
    public void BeginFadeOut()
    {
        if (_stopFadeActive || _finishedRaised) return;
        _stopFadeActive = true;
        _stopFadeStartFrame = _samplesEmitted;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_finishedRaised)
        {
            return 0;
        }
        var n = _chain.Read(buffer, offset, count);
        if (n > 0)
        {
            ApplyEnvelope(buffer, offset, n);
            _samplesEmitted += n / _channels;
        }

        // Stop-triggered fade-out completed → emit Finished so the engine
        // can detach us from the mixer.
        if (_stopFadeActive && !_finishedRaised &&
            _samplesEmitted - _stopFadeStartFrame >= _fadeOutSamples)
        {
            _finishedRaised = true;
            Finished?.Invoke(this);
            return n;
        }

        // EOF detection: when the raw underlying source is exhausted AND the
        // resampler has drained its tail, Read returns 0. Some resamplers
        // sometimes pad with silence — RawFloatStream signaling EOF is the
        // authoritative check, but the resampler may still buffer a few
        // samples after that. Using `_raw.IsEof && n == 0` covers both.
        if (n == 0 || (_raw.IsEof && n < count))
        {
            if (!_finishedRaised)
            {
                _finishedRaised = true;
                Finished?.Invoke(this);
            }
        }
        return n;
    }

    /// <summary>
    /// Apply fade-in (start of stream), natural fade-out (last <see cref="FadeOutMs"/>
    /// before EOF), and stop-triggered fade-out envelopes to the buffer in place.
    /// All envelopes are linear and operate per-frame (same gain across channels).
    /// </summary>
    private void ApplyEnvelope(float[] buffer, int offset, int sampleCount)
    {
        var frames = sampleCount / _channels;
        // Natural fade-out point: the resampler/upmix chain may not let us
        // know exactly where EOF is, but we can estimate from the raw stream.
        // We compute it lazily inside the loop to avoid extra branches.
        var totalRawSamples = _cached.PcmBytes.Length / sizeof(float);
        var totalRawFrames = totalRawSamples / _cached.Format.Channels;
        // Convert raw frames to working-rate frames so the natural-fade
        // window aligns with the output sample rate.
        var totalOutFrames = (long)((double)totalRawFrames * WaveFormat.SampleRate / _cached.Format.SampleRate);
        var naturalFadeStart = totalOutFrames - _fadeOutSamples;
        if (naturalFadeStart < 0) naturalFadeStart = 0;

        for (int f = 0; f < frames; f++)
        {
            var frameIdx = _samplesEmitted + f;
            float gain = 1f;

            // Fade-in: first _fadeInSamples per-channel frames ramp 0 → 1.
            if (frameIdx < _fadeInSamples)
            {
                var fadeInGain = _fadeInSamples == 0 ? 1f : (float)frameIdx / _fadeInSamples;
                if (fadeInGain < gain) gain = fadeInGain;
            }

            // Natural fade-out: last _fadeOutSamples frames ramp 1 → 0.
            if (_fadeOutSamples > 0 && frameIdx >= naturalFadeStart && frameIdx < totalOutFrames)
            {
                var remaining = totalOutFrames - frameIdx;
                var natFadeGain = (float)remaining / _fadeOutSamples;
                if (natFadeGain < gain) gain = natFadeGain;
            }

            // Stop-triggered fade-out: ramps 1 → 0 over _fadeOutSamples
            // starting at the frame BeginFadeOut was called.
            if (_stopFadeActive && _fadeOutSamples > 0)
            {
                var elapsed = frameIdx - _stopFadeStartFrame;
                if (elapsed >= _fadeOutSamples) { gain = 0f; }
                else if (elapsed >= 0)
                {
                    var stopGain = 1f - (float)elapsed / _fadeOutSamples;
                    if (stopGain < gain) gain = stopGain;
                }
            }

            if (gain >= 1f) continue;
            var baseIdx = offset + f * _channels;
            for (int c = 0; c < _channels; c++)
            {
                buffer[baseIdx + c] *= gain;
            }
        }
    }

    private static ISampleProvider EnsureStereo(ISampleProvider mono) =>
        mono.WaveFormat.Channels == 1 ? new MonoToStereoSampleProvider(mono) : mono;

    /// <summary>
    /// Reads 32-bit IEEE float samples directly out of a <see cref="CachedSound"/>'s
    /// byte buffer. Avoids the overhead of going through a MemoryStream +
    /// RawSourceWaveStream + ToSampleProvider chain when we already know the
    /// data is float PCM.
    /// </summary>
    private sealed class RawFloatStream : ISampleProvider
    {
        private readonly CachedSound _cached;
        private int _bytePosition;

        public RawFloatStream(CachedSound cached)
        {
            _cached = cached;
            // The NAudioSoundDecoder always stores 32-bit IEEE float; assert.
            if (cached.Format.Encoding != WaveFormatEncoding.IeeeFloat || cached.Format.BitsPerSample != 32)
                throw new ArgumentException($"CachedSound must be 32-bit IEEE float (got {cached.Format.Encoding}/{cached.Format.BitsPerSample})", nameof(cached));
            WaveFormat = cached.Format;
        }

        public WaveFormat WaveFormat { get; }
        public bool IsEof => _bytePosition >= _cached.PcmBytes.Length;

        public int Read(float[] buffer, int offset, int count)
        {
            var bytesRemaining = _cached.PcmBytes.Length - _bytePosition;
            if (bytesRemaining <= 0) return 0;
            var bytesToCopy = Math.Min(count * sizeof(float), bytesRemaining);
            // Align to 4-byte (float) boundary to avoid splitting a sample.
            bytesToCopy -= bytesToCopy % sizeof(float);
            if (bytesToCopy <= 0) return 0;
            Buffer.BlockCopy(_cached.PcmBytes, _bytePosition, buffer, offset * sizeof(float), bytesToCopy);
            _bytePosition += bytesToCopy;
            return bytesToCopy / sizeof(float);
        }
    }
}
