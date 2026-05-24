namespace Soundpad.Audio;

/// <summary>
/// Pure, dependency-free loudness analysis. Computes an RMS-based gain (dB) to
/// bring a clip toward a target level — a cheap approximation of perceived
/// loudness (true LUFS is overkill here). Input is interleaved IEEE-float PCM
/// bytes (the format Reson caches decoded sounds in).
/// </summary>
public static class LoudnessAnalyzer
{
    /// <summary>
    /// Returns the gain in dB to apply so the clip's RMS reaches targetDb,
    /// clamped to [-60, maxBoostDb]. Silence / empty input returns 0 (no change),
    /// never +infinity.
    /// </summary>
    public static double ComputeGainDb(byte[] pcmFloatBytes, int channels, double targetDb, double maxBoostDb)
    {
        if (pcmFloatBytes.Length < 4) return 0.0;
        int sampleCount = pcmFloatBytes.Length / 4;
        double sumSquares = 0;
        var span = pcmFloatBytes.AsSpan();
        for (int i = 0; i < sampleCount; i++)
        {
            float s = BitConverter.ToSingle(span.Slice(i * 4, 4));
            sumSquares += (double)s * s;
        }
        double rms = Math.Sqrt(sumSquares / sampleCount);
        if (rms <= 1e-6) return 0.0; // effectively silence — don't amplify

        double rmsDb = 20.0 * Math.Log10(rms);
        double gain = targetDb - rmsDb;
        return Math.Clamp(gain, -60.0, maxBoostDb);
    }

    /// <summary>Convert a dB gain to a linear multiplier.</summary>
    public static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);
}
