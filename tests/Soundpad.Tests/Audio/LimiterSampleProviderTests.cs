using FluentAssertions;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class LimiterSampleProviderTests
{
    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;
        public ArraySampleProvider(WaveFormat fmt, float[] data) { WaveFormat = fmt; _data = data; }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _data.Length - _pos);
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    private static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    [Fact]
    public void Samples_Above_One_Are_Clamped_To_One()
    {
        var src = new ArraySampleProvider(Fmt, new[] { 1.5f, 2f, 999f, 1.0001f });
        var lim = new LimiterSampleProvider(src);
        var buf = new float[4];
        lim.Read(buf, 0, buf.Length);
        buf.Should().AllSatisfy(s => s.Should().Be(1f));
    }

    [Fact]
    public void Samples_Below_Negative_One_Are_Clamped_To_Negative_One()
    {
        var src = new ArraySampleProvider(Fmt, new[] { -1.5f, -2f, -999f, -1.0001f });
        var lim = new LimiterSampleProvider(src);
        var buf = new float[4];
        lim.Read(buf, 0, buf.Length);
        buf.Should().AllSatisfy(s => s.Should().Be(-1f));
    }

    [Fact]
    public void Samples_In_Range_Pass_Through_Unchanged()
    {
        var input = new[] { 0f, 0.123f, -0.456f, 0.999f, -0.999f, 1f, -1f, 0.5f };
        var src = new ArraySampleProvider(Fmt, input);
        var lim = new LimiterSampleProvider(src);
        var buf = new float[input.Length];
        lim.Read(buf, 0, buf.Length);
        buf.Should().Equal(input);
    }

    [Fact]
    public void Mixed_Samples_Are_Clamped_Independently()
    {
        var src = new ArraySampleProvider(Fmt, new[] { 2f, 0.5f, -3f, -0.25f, 0f, 1.1f });
        var lim = new LimiterSampleProvider(src);
        var buf = new float[6];
        lim.Read(buf, 0, buf.Length);
        buf.Should().Equal(new[] { 1f, 0.5f, -1f, -0.25f, 0f, 1f });
    }

    [Fact]
    public void Read_Honors_Offset_And_Count_And_Preserves_Untouched_Bytes()
    {
        // Pre-fill the buffer with a sentinel; the limiter must not write
        // outside the [offset, offset+returned) window.
        var src = new ArraySampleProvider(Fmt, new[] { 2f, -2f });
        var lim = new LimiterSampleProvider(src);
        var buf = new float[8];
        for (int i = 0; i < buf.Length; i++) buf[i] = 42f;
        var n = lim.Read(buf, 3, 2);
        n.Should().Be(2);
        buf[3].Should().Be(1f);
        buf[4].Should().Be(-1f);
        buf[0].Should().Be(42f);
        buf[2].Should().Be(42f);
        buf[5].Should().Be(42f);
        buf[7].Should().Be(42f);
    }

    [Fact]
    public void WaveFormat_Mirrors_Source()
    {
        var src = new ArraySampleProvider(Fmt, Array.Empty<float>());
        var lim = new LimiterSampleProvider(src);
        lim.WaveFormat.Should().BeSameAs(Fmt);
    }
}
