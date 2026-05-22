using NAudio.Wave;

namespace Soundpad.Audio;

/// <summary>
/// Hard-clamp limiter wrapping another <see cref="ISampleProvider"/>.
/// Clamps each sample to <c>[-1.0, 1.0]</c> to prevent clipping/wraparound
/// when loud sources are summed (e.g. hot mic + hot sound) before they hit
/// the WASAPI buffer. Samples already inside <c>[-1, 1]</c> pass through
/// unchanged so the limiter is inaudible on normal material.
/// </summary>
/// <remarks>
/// We deliberately use a hard clamp (not a soft knee) here: at the post-mix
/// position the goal is purely "don't overflow the float-to-PCM conversion"
/// and a transparent ceiling is more important than smooth gain reduction.
/// A soft knee at 0.9 was prototyped but slightly altered timbre on already
/// loud mp3s; hard clamp leaves them untouched until they actually exceed 1.
/// </remarks>
public sealed class LimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public LimiterSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = _source.Read(buffer, offset, count);
        var end = offset + n;
        for (int i = offset; i < end; i++)
        {
            var s = buffer[i];
            if (s > 1f) buffer[i] = 1f;
            else if (s < -1f) buffer[i] = -1f;
        }
        return n;
    }
}
