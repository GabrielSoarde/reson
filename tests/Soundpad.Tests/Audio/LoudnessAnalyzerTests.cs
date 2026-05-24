using FluentAssertions;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class LoudnessAnalyzerTests
{
    // Build interleaved-stereo IEEE-float PCM bytes from a constant amplitude.
    private static byte[] ConstantPcm(float amplitude, int frames = 4800, int channels = 2)
    {
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++) samples[i] = amplitude;
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Gain_Boosts_Quiet_Clip_Toward_Target()
    {
        // amplitude 0.01 → RMS ≈ -40 dBFS. Target -20 → wants +20 dB, capped to +12.
        var db = LoudnessAnalyzer.ComputeGainDb(ConstantPcm(0.01f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().BeApproximately(12.0, 0.5); // capped
    }

    [Fact]
    public void Gain_Attenuates_Loud_Clip_Toward_Target()
    {
        // amplitude 0.5 → RMS ≈ -6 dBFS. Target -20 → wants about -14 dB.
        var db = LoudnessAnalyzer.ComputeGainDb(ConstantPcm(0.5f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().BeApproximately(-14.0, 0.5);
    }

    [Fact]
    public void Silence_Returns_Zero_Gain_Not_Infinite()
    {
        var db = LoudnessAnalyzer.ComputeGainDb(ConstantPcm(0f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().Be(0.0); // never amplify silence
    }

    [Fact]
    public void Empty_Pcm_Returns_Zero_Gain()
    {
        LoudnessAnalyzer.ComputeGainDb(Array.Empty<byte>(), 2, -20, 12).Should().Be(0.0);
    }

    // The constant-amplitude tests above use DC, where RMS == amplitude. Real
    // audio is AC: a sine of peak A has RMS = A/√2 (≈ -3 dB below its peak).
    // This case exercises the actual RMS path so a peak-vs-RMS mixup can't slip
    // through. Peak 0.5 sine → RMS ≈ 0.3536 → -9.03 dBFS → target -20 wants ≈ -11 dB.
    private static byte[] SinePcm(float peak, int frames = 48000, int channels = 2, double freq = 440, int rate = 48000)
    {
        var samples = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(peak * Math.Sin(2 * Math.PI * freq * f / rate));
            for (int c = 0; c < channels; c++) samples[f * channels + c] = v;
        }
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Gain_Uses_Rms_Not_Peak_For_Sine()
    {
        // If the analyzer wrongly used peak (0.5 → -6 dBFS) it would compute -14;
        // correct RMS (0.3536 → -9.03 dBFS) computes ≈ -10.97.
        var db = LoudnessAnalyzer.ComputeGainDb(SinePcm(0.5f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().BeApproximately(-10.97, 0.5);
    }

    [Fact]
    public void NonFinite_Samples_Return_Zero_Gain()
    {
        // A malformed decoded clip could produce NaN or Inf samples. Because the
        // resulting gain is persisted to config.json and fed into DbToLinear, a
        // non-finite result would silently break the sound permanently.
        var samples = new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f };
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        LoudnessAnalyzer.ComputeGainDb(bytes, channels: 2, targetDb: -20, maxBoostDb: 12)
            .Should().Be(0.0);
    }
}
