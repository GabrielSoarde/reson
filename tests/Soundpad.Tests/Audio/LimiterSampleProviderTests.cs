using FluentAssertions;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class LimiterSampleProviderTests
{
    // ISampleProvider that replays a fixed buffer once.
    private sealed class ArraySource : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;
        public ArraySource(float[] data) { _data = data; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    private static float[] Limit(float[] input, double threshold)
    {
        var limiter = new LimiterSampleProvider(new ArraySource(input), threshold);
        var outBuf = new float[input.Length];
        int total = 0;
        while (total < input.Length)
        {
            int n = limiter.Read(outBuf, total, input.Length - total);
            if (n == 0) break;
            total += n;
        }
        return outBuf;
    }

    // Curve-shape tests use an explicit threshold of 0.5 — clean, exactly
    // representable in float, far from any epsilon-boundary fragility. The
    // shipping default (0.98) is verified separately in Default_Threshold_*.

    [Fact]
    public void Below_Threshold_Passes_Through_Bit_Exact()
    {
        // All strictly below 0.5, plus 0.5 itself (exactly representable in
        // float, so the <= boundary comparison is bit-safe).
        var input = new[] { 0f, 0.1f, -0.3f, 0.49f, 0.5f, -0.5f };
        var outp = Limit(input, threshold: 0.5);
        outp.Should().Equal(input);
    }

    [Fact]
    public void Above_Threshold_Is_Compressed_Strictly_Below_One()
    {
        var outp = Limit(new[] { 1.0f, 2.0f, 5.0f }, threshold: 0.5);
        foreach (var s in outp) s.Should().BeInRange(0.5f, 0.99999f); // above knee, never reaches 1.0
    }

    [Fact]
    public void Negative_Is_Symmetric()
    {
        var pos = Limit(new[] { 3.0f }, 0.5)[0];
        var neg = Limit(new[] { -3.0f }, 0.5)[0];
        neg.Should().BeApproximately(-pos, 1e-6f);
    }

    [Fact]
    public void Never_Exceeds_Unit_Range_For_Extreme_Input()
    {
        var outp = Limit(new[] { 100f, -100f, 10f, -10f }, 0.5);
        foreach (var s in outp) Math.Abs(s).Should().BeLessThanOrEqualTo(1.0f);
    }

    [Fact]
    public void Monotonic_Above_Threshold()
    {
        var a = Limit(new[] { 0.6f }, 0.5)[0];
        var b = Limit(new[] { 1.0f }, 0.5)[0];
        var c = Limit(new[] { 3.0f }, 0.5)[0];
        a.Should().BeLessThan(b);
        b.Should().BeLessThan(c);
    }

    [Fact]
    public void Continuous_At_Threshold()
    {
        // At T (=0.5, exact float) output is exactly T. Just above, output stays
        // very close to T — no jump. 0.5001f is a distinct float from 0.5f, and
        // the delta is well above float epsilon, so this isn't a tautology.
        var atT = Limit(new[] { 0.5f }, 0.5)[0];
        var justAbove = Limit(new[] { 0.5001f }, 0.5)[0];
        atT.Should().Be(0.5f);
        justAbove.Should().BeApproximately(0.5f, 0.01f);
        justAbove.Should().BeGreaterThan(atT); // strictly increasing past the knee
    }

    private static float ReadOne(LimiterSampleProvider lim)
    {
        var b = new float[1];
        lim.Read(b, 0, 1);
        return b[0];
    }

    [Fact]
    public void Default_Threshold_Is_Near_098()
    {
        // 0.97 is below the default 0.98 knee → untouched; 0.99 is above → compressed.
        ReadOne(new LimiterSampleProvider(new ArraySource(new[] { 0.97f }))).Should().Be(0.97f);
        var hot = ReadOne(new LimiterSampleProvider(new ArraySource(new[] { 0.99f })));
        hot.Should().BeInRange(0.98f, 0.99f);
        hot.Should().BeLessThan(0.99f); // compressed, not pass-through
    }

    [Fact]
    public void Continuous_Waveform_Rolls_Off_Without_Plateau()
    {
        // The case T6 exists for: a continuous waveform whose peaks exceed 1.0
        // after an F1 boost. A hard clamp would flat-top every over-unit sample
        // to exactly 1.0 (a plateau = audible distortion). The soft knee maps
        // each distinct input to a distinct output, so the crest stays curved.
        int rate = 48000, frames = rate / 100; // ~one 100 Hz cycle
        var sine = new float[frames];
        for (int i = 0; i < frames; i++)
            sine[i] = (float)(1.3 * Math.Sin(2 * Math.PI * i / frames)); // peak 1.3

        var outp = Limit(sine, threshold: 0.98);

        // (a) never exceeds the ceiling
        foreach (var s in outp) Math.Abs(s).Should().BeLessThanOrEqualTo(1.0f);

        // (b) the over-threshold region is NOT a flat plateau: the outputs from
        // inputs above 0.98 take many distinct values (a hard clamp would make
        // them all identical at 1.0).
        var overKnee = new List<float>();
        for (int i = 0; i < frames; i++)
            if (sine[i] > 0.98f) overKnee.Add(outp[i]);
        overKnee.Should().HaveCountGreaterThan(3);
        overKnee.Distinct().Count().Should().BeGreaterThan(overKnee.Count / 2); // mostly distinct, no plateau
    }
}
