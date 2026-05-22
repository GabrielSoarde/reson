using FluentAssertions;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class SoundSampleProviderTests
{
    private static CachedSound MakeConstantSound(float value, int totalSamples)
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var pcm = new byte[totalSamples * sizeof(float)];
        for (int i = 0; i < totalSamples; i++)
            BitConverter.GetBytes(value).CopyTo(pcm, i * sizeof(float));
        return new CachedSound(fmt, pcm);
    }

    [Fact]
    public void Read_Emits_Cached_Samples_At_Configured_Volume()
    {
        var cached = MakeConstantSound(0.5f, totalSamples: 1024);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), initialVolume: 1.0f);
        var buf = new float[256];
        var n = p.Read(buf, 0, buf.Length);
        n.Should().Be(256);
        buf.Should().AllSatisfy(s => s.Should().Be(0.5f));
    }

    [Fact]
    public void Volume_Scales_Output_Samples()
    {
        var cached = MakeConstantSound(1.0f, totalSamples: 1024);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), initialVolume: 0.25f);
        var buf = new float[64];
        p.Read(buf, 0, buf.Length);
        buf.Should().AllSatisfy(s => s.Should().BeApproximately(0.25f, 1e-6f));
    }

    [Fact]
    public void Volume_Adjustable_At_Runtime()
    {
        var cached = MakeConstantSound(1.0f, totalSamples: 8192);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), initialVolume: 1.0f);
        var buf = new float[64];
        p.Read(buf, 0, buf.Length);
        buf[0].Should().Be(1.0f);
        p.Volume = 0.5f;
        p.Read(buf, 0, buf.Length);
        buf[0].Should().Be(0.5f);
    }

    [Fact]
    public void Finished_Raised_Exactly_Once_At_EOF()
    {
        var cached = MakeConstantSound(0.5f, totalSamples: 128);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var finishedCount = 0;
        p.Finished += _ => finishedCount++;
        var buf = new float[256];
        // First read consumes everything; second read should be 0 and not re-raise.
        p.Read(buf, 0, buf.Length);
        p.Read(buf, 0, buf.Length);
        p.Read(buf, 0, buf.Length);
        finishedCount.Should().Be(1);
        p.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void Two_Providers_Have_Independent_Read_Positions()
    {
        var cached = MakeConstantSound(0.5f, totalSamples: 1024);
        var workingFmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var a = new SoundSampleProvider(cached, workingFmt);
        var b = new SoundSampleProvider(cached, workingFmt);

        var buf = new float[256];
        a.Read(buf, 0, buf.Length); // a's position now at 256
        b.Read(buf, 0, buf.Length); // b's position still moves independently
        a.IsFinished.Should().BeFalse();
        b.IsFinished.Should().BeFalse();

        // Drain b completely; a should still have samples left.
        while (b.Read(buf, 0, buf.Length) > 0) { }
        b.IsFinished.Should().BeTrue();
        a.IsFinished.Should().BeFalse();
    }

    [Fact]
    public void Resamples_When_Source_Rate_Differs_From_Working()
    {
        // Source is 44.1k mono; working format is 48k stereo. The chain
        // should resample + up-mix.
        var sourceFmt = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        var pcm = new byte[4096 * sizeof(float)];
        for (int i = 0; i < 4096; i++) BitConverter.GetBytes(0.5f).CopyTo(pcm, i * sizeof(float));
        var cached = new CachedSound(sourceFmt, pcm);

        var workingFmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var p = new SoundSampleProvider(cached, workingFmt);
        p.WaveFormat.SampleRate.Should().Be(48000);
        p.WaveFormat.Channels.Should().Be(2);

        var buf = new float[512];
        var n = p.Read(buf, 0, buf.Length);
        n.Should().BeGreaterThan(0);
    }
}
