using NAudio.Wave;

namespace Soundpad.Audio;

/// <summary>
/// Soft-knee limiter wrapping another <see cref="ISampleProvider"/>, on the
/// post-mix game output before WASAPI. Samples at or below <c>threshold</c>
/// (default 0.98) pass through bit-exact. Above it, the excess is smoothly
/// compressed via tanh and asymptotes to ±1.0, so output never reaches or
/// exceeds the ±1.0 ceiling and loud/boosted material rolls off instead of
/// hard-clipping into audible distortion.
/// </summary>
/// <remarks>
/// Transfer function for |s| &gt; T:  out = sign(s)·(T + K·tanh(|s|−T)),  where K = 1−T.
/// The excess is the raw value (|s|−T), intentionally not divided by K — dividing
/// would saturate tanh for small K (e.g. K=0.02 at T=0.98) and recreate a plateau
/// at the locked threshold. Continuous at T, monotonic, bounded in (−1, 1) above
/// the knee. Replaces the earlier hard clamp (which prevented overflow but distorted
/// on hot material — the exact symptom this fixes, now that F1 normalization can
/// boost +12 dB). Default threshold is 0.98 (near the ceiling) so the limiter only
/// engages on material genuinely approaching 0 dBFS, leaving everything below untouched.
/// </remarks>
public sealed class LimiterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _threshold;
    private readonly float _knee; // 1 - threshold

    public LimiterSampleProvider(ISampleProvider source, double threshold = 0.98)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _threshold = (float)threshold;
        _knee = 1f - _threshold;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _source.Read(buffer, offset, count);
        int end = offset + n;
        for (int i = offset; i < end; i++)
        {
            float s = buffer[i];
            float mag = Math.Abs(s);
            if (mag <= _threshold) continue; // bit-exact pass-through
            // Transfer function: out = T + K·tanh(|s| − T)
            //   where K = 1 − T  ("knee width", remaining headroom above threshold).
            // Note: the tanh argument is the raw excess (|s| − T), NOT divided by K.
            // Dividing by K would make the knee extremely steep for thresholds near
            // 1 (e.g. K=0.02 at T=0.98), causing tanh to saturate to 1.0f for even
            // small overages and recreating a plateau — the exact distortion we are
            // fixing.  Using raw excess keeps the compression curve smooth across the
            // full over-threshold range (bounded: as |s|→∞, tanh→1, out→T+K=1).
            // Double precision avoids MathF.Tanh rounding to exactly 1.0f prematurely.
            double excess = (double)mag - _threshold;
            double compressed = _threshold + _knee * Math.Tanh(excess);
            // Guard: analytically bounded in (T, 1) but clamp in case double→float
            // rounding reaches 1.0f on extremely large inputs (|s| ≫ 1).
            float clamped = (float)Math.Min(compressed, (double)MathF.BitDecrement(1.0f));
            buffer[i] = s < 0 ? -clamped : clamped;
        }
        return n;
    }
}
