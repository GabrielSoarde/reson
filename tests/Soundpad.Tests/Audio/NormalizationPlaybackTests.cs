using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;
using Soundpad.Sound;

namespace Soundpad.Tests.Audio;

public class NormalizationPlaybackTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "reson-normpb-" + Guid.NewGuid());
    public NormalizationPlaybackTests() { Directory.CreateDirectory(Path.Combine(_tempDir, "sounds")); }
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition not met in time");
    }

    private static CachedSound MakeCached(float amplitude)
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var samples = new float[48000 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = amplitude;
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new CachedSound(fmt, bytes);
    }

    private (PlaybackEngine engine, SoundLibrary lib, List<FakeWavePlayer> players) Build(float amplitude, bool normalize = true)
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        if (!normalize) lib.SetNormalizeEnabled(false);
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>())).Returns(MakeCached(amplitude));
        var cache = new SoundCache(decoder.Object, 50);
        var players = new List<FakeWavePlayer>();
        var factory = new Mock<IWavePlayerFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string n, int l) => { var p = new FakeWavePlayer(n); players.Add(p); return p; });
        var engine = new PlaybackEngine(factory.Object, cache, lib); // 3-arg ctor (mic null)
        engine.SetGameDevice("game");
        engine.SetVolume(100);
        engine.Start();
        return (engine, lib, players);
    }

    [Fact]
    public async Task First_Play_Computes_And_Persists_NormalizeGain()
    {
        var (engine, lib, _) = Build(0.01f);          // 0.01 DC → -40 dBFS → wants +20, capped at +12
        var up = lib.Upload("a.mp3", new MemoryStream(new byte[10]));
        engine.Play(up.Entry.Id, Path.Combine(_tempDir, "sounds", up.FinalFilename));

        // Poll the persisted outcome — the persist is backgrounded, so wait for it.
        await WaitUntil(() => lib.ActiveBoard.Sounds.Single(s => s.Id == up.Entry.Id).NormalizeGainDb is not null);
        lib.ActiveBoard.Sounds.Single(s => s.Id == up.Entry.Id).NormalizeGainDb.Should().BeApproximately(12.0, 0.5);
        engine.Shutdown();
    }

    [Fact]
    public async Task Normalize_Disabled_Does_Not_Compute_Gain()
    {
        var (engine, lib, _) = Build(0.01f, normalize: false);
        var up = lib.Upload("b.mp3", new MemoryStream(new byte[10]));
        engine.Play(up.Entry.Id, Path.Combine(_tempDir, "sounds", up.FinalFilename));
        await Task.Delay(200); // brief settle — asserting a NON-event
        lib.ActiveBoard.Sounds.Single(s => s.Id == up.Entry.Id).NormalizeGainDb.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Volume_Change_Mid_Play_Preserves_Normalization()
    {
        var (engine, lib, players) = Build(0.01f);
        var up = lib.Upload("c.mp3", new MemoryStream(new byte[10]));
        engine.Play(up.Entry.Id, Path.Combine(_tempDir, "sounds", up.FinalFilename));
        await WaitUntil(() => engine.ActiveGameEffectiveVolumeForTests is not null);

        var beforeNorm = engine.ActiveNormalizeLinearForTests;   // the normalize multiplier in effect
        beforeNorm.Should().BeApproximately(LoudnessAnalyzer.DbToLinear(12.0), 0.01f);

        // Move the master fader mid-play; normalization must survive.
        engine.SetVolume(50);
        await WaitUntil(() => Math.Abs(engine.ActiveGameEffectiveVolumeForTests!.Value
                                       - (0.5f * 1f * beforeNorm)) < 0.001f);
        engine.Shutdown();
    }
}
