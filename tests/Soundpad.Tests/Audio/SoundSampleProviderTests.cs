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

    // Working format used throughout these tests. SoundSampleProvider applies
    // a 10 ms fade-in at the start and a 20 ms fade-out at the end of every
    // stream. At 48 kHz stereo that's 480 frames (960 samples) of fade-in and
    // 960 frames (1920 samples) of fade-out — we skip past those windows when
    // measuring steady-state amplitude.
    private const int FadeInSamples48kStereo = 960;
    private const int FadeOutSamples48kStereo = 1920;

    [Fact]
    public void Read_Emits_Cached_Samples_At_Configured_Volume()
    {
        // Make the sound long enough that we can skip fade-in and still read
        // a steady-state window before fade-out kicks in.
        var cached = MakeConstantSound(0.5f, totalSamples: 16384);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), initialVolume: 1.0f);
        // Skip past the fade-in window.
        var skip = new float[FadeInSamples48kStereo];
        p.Read(skip, 0, skip.Length);
        var buf = new float[256];
        var n = p.Read(buf, 0, buf.Length);
        n.Should().Be(256);
        buf.Should().AllSatisfy(s => s.Should().Be(0.5f));
    }

    [Fact]
    public void Volume_Scales_Output_Samples()
    {
        var cached = MakeConstantSound(1.0f, totalSamples: 16384);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), initialVolume: 0.25f);
        var skip = new float[FadeInSamples48kStereo];
        p.Read(skip, 0, skip.Length);
        var buf = new float[64];
        p.Read(buf, 0, buf.Length);
        buf.Should().AllSatisfy(s => s.Should().BeApproximately(0.25f, 1e-6f));
    }

    [Fact]
    public void Volume_Adjustable_At_Runtime()
    {
        var cached = MakeConstantSound(1.0f, totalSamples: 16384);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), initialVolume: 1.0f);
        var skip = new float[FadeInSamples48kStereo];
        p.Read(skip, 0, skip.Length);
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
    public void FadeIn_Starts_At_Zero_And_Ramps_To_Full()
    {
        var cached = MakeConstantSound(1.0f, totalSamples: 16384);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var buf = new float[FadeInSamples48kStereo + 256];
        var n = p.Read(buf, 0, buf.Length);
        n.Should().Be(buf.Length);
        // First frame: gain == 0 → sample == 0.
        buf[0].Should().Be(0f);
        // Mid-fade (frame index 240 → gain ≈ 0.5).
        buf[240 * 2].Should().BeApproximately(0.5f, 0.01f);
        // After fade-in: gain == 1.
        buf[FadeInSamples48kStereo + 64].Should().Be(1f);
    }

    [Fact]
    public void FadeOut_Naturally_Applied_At_End_Of_Stream()
    {
        // Sound is exactly the fade-out window long: every sample should be
        // attenuated (no plateau in the middle).
        var totalFrames = FadeOutSamples48kStereo / 2; // 960 frames
        var cached = MakeConstantSound(1.0f, totalSamples: totalFrames * 2); // stereo
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var buf = new float[totalFrames * 2];
        var n = p.Read(buf, 0, buf.Length);
        n.Should().Be(buf.Length);
        // Last sample must be (near) 0 — natural fade reduces tail to silence.
        buf[^1].Should().BeApproximately(0f, 0.01f);
    }

    [Fact]
    public void BeginFadeOut_Triggers_Finished_After_FadeOut_Window()
    {
        // Long sound, normally far past the fade-in.
        var cached = MakeConstantSound(1.0f, totalSamples: 480000); // 5 sec
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var skip = new float[FadeInSamples48kStereo + 4096];
        p.Read(skip, 0, skip.Length);
        var finishedCount = 0;
        p.Finished += _ => finishedCount++;

        p.BeginFadeOut();
        p.IsFadingOut.Should().BeTrue();
        // Read one block partially into the fade window → no Finished yet.
        var midBuf = new float[256];
        p.Read(midBuf, 0, midBuf.Length);
        finishedCount.Should().Be(0);
        // Read enough to exceed 20 ms (FadeOutSamples48kStereo samples = 960 frames).
        var rest = new float[FadeOutSamples48kStereo + 256];
        p.Read(rest, 0, rest.Length);
        finishedCount.Should().Be(1);
        p.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void BeginFadeOut_Ramps_Toward_Zero_Linearly()
    {
        var cached = MakeConstantSound(1.0f, totalSamples: 480000);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        // Skip well past fade-in so we know steady state == 1.0.
        var skip = new float[FadeInSamples48kStereo + 4096];
        p.Read(skip, 0, skip.Length);
        p.BeginFadeOut();
        var buf = new float[FadeOutSamples48kStereo];
        p.Read(buf, 0, buf.Length);
        // First sample after fade-out begins: gain ≈ 1.0.
        buf[0].Should().BeApproximately(1f, 0.01f);
        // Mid fade (frame 480 of 960): gain ≈ 0.5.
        buf[480 * 2].Should().BeApproximately(0.5f, 0.02f);
        // Last sample of fade-out window: gain ≈ 0.
        buf[^1].Should().BeApproximately(0f, 0.01f);
    }

    [Fact]
    public void BeginFadeOut_Idempotent()
    {
        var cached = MakeConstantSound(1.0f, totalSamples: 480000);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var skip = new float[FadeInSamples48kStereo + 4096];
        p.Read(skip, 0, skip.Length);
        p.BeginFadeOut();
        // Read partway into the fade window.
        var part = new float[256];
        p.Read(part, 0, part.Length);
        // Second BeginFadeOut must NOT reset the start frame (which would
        // restart the ramp from 1.0 — that's worse than the click we're
        // trying to avoid).
        p.BeginFadeOut();
        var rest = new float[FadeOutSamples48kStereo];
        var finishedCount = 0;
        p.Finished += _ => finishedCount++;
        p.Read(rest, 0, rest.Length);
        // Within the original fade window the sound should have finished.
        finishedCount.Should().Be(1);
    }

    [Fact]
    public void Sound_Shorter_Than_FadeIn_Applies_Fade_Proportionally()
    {
        // 200-frame sound, well shorter than the 480-frame fade-in. The
        // natural fade-out also covers the whole window (since the stream is
        // shorter than the fade-out length), so the envelope at frame f is
        // min(f/480, remaining/960). We assert the shape rather than monotonicity.
        var cached = MakeConstantSound(1.0f, totalSamples: 200 * 2);
        var p = new SoundSampleProvider(cached, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var buf = new float[200 * 2];
        var n = p.Read(buf, 0, buf.Length);
        n.Should().Be(buf.Length);
        // First sample: gain == 0.
        buf[0].Should().Be(0f);
        // Last sample: natural fade-out gain = 1/960 ≈ 0.001.
        buf[^1].Should().BeLessOrEqualTo(0.01f);
        // Every sample must be strictly less than 1.0 (we never hit the
        // fade-in's full-gain plateau since the sound ends first).
        buf.Should().AllSatisfy(s => s.Should().BeLessThan(1f));
        // Peak occurs around the crossover of fade-in/fade-out
        // (f/480 == (200-f)/960 → f ≈ 67, gain ≈ 67/480 ≈ 0.14).
        buf.Max().Should().BeGreaterThan(0.1f);
        buf.Max().Should().BeLessThan(0.5f);
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
