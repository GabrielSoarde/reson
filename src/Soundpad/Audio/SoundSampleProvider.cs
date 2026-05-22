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
    private readonly CachedSound _cached;
    private readonly ISampleProvider _chain;
    private readonly RawFloatStream _raw;
    private readonly VolumeSampleProvider _volume;
    private bool _finishedRaised;

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
    }

    public WaveFormat WaveFormat { get; }

    public float Volume
    {
        get => _volume.Volume;
        set => _volume.Volume = value;
    }

    /// <summary>True once <see cref="Finished"/> has been raised.</summary>
    public bool IsFinished => _finishedRaised;

    /// <summary>
    /// Raised when the cached PCM is fully consumed. Always raised exactly
    /// once, on the thread that called <see cref="Read"/> at the moment EOF
    /// is reached.
    /// </summary>
    public event Action<SoundSampleProvider>? Finished;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_finishedRaised)
        {
            return 0;
        }
        var n = _chain.Read(buffer, offset, count);
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
