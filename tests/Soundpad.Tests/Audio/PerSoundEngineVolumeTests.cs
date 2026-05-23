using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;
using Soundpad.Sound;

namespace Soundpad.Tests.Audio;

/// <summary>
/// PlaybackEngine applies per-sound Volume as a multiplier on top of the master
/// fader: effective = (masterVol / 100) * (soundVol / 100). The math is the
/// load-bearing piece — the SoundSampleProvider applies the resulting float
/// to its sample buffer downstream.
/// </summary>
public class PerSoundEngineVolumeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-engine-vol-" + Guid.NewGuid());

    public PerSoundEngineVolumeTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "a.mp3"), new byte[10]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (PlaybackEngine Engine, SoundLibrary Library, string SoundId) Build()
    {
        var library = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        library.Load();
        library.AutoScan();
        var id = library.ActiveBoard.Sounds.Single().Id;

        var factory = new Mock<IWavePlayerFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => new FakeWavePlayer(name));
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[4096]));
        var cache = new SoundCache(decoder.Object, 50);
        var engine = new PlaybackEngine(factory.Object, cache, new FakeMicCapture(), library);
        engine.SetGameDevice("G");
        engine.SetVolume(100);
        engine.Start();
        return (engine, library, id);
    }

    [Fact]
    public async Task Per_Sound_Volume_Caps_Effective_Volume()
    {
        var (engine, library, id) = Build();
        try
        {
            library.UpdateSound(id, volume: 50);
            engine.Play(id, Path.Combine(_tempDir, "sounds", library.ActiveBoard.Sounds[0].File));
            await Task.Delay(150);

            // Master is 100% and per-sound is 50% → effective should be 0.5.
            engine.ActiveGameSoundForTests.Should().NotBeNull();
            engine.ActiveGameSoundForTests!.Volume.Should().BeApproximately(0.5f, 0.001f);
        }
        finally { engine.Shutdown(); }
    }

    [Fact]
    public async Task Per_Sound_Volume_Multiplies_With_Master_Volume()
    {
        var (engine, library, id) = Build();
        try
        {
            library.UpdateSound(id, volume: 80);
            engine.SetVolume(50);
            await Task.Delay(50);
            engine.Play(id, Path.Combine(_tempDir, "sounds", library.ActiveBoard.Sounds[0].File));
            await Task.Delay(150);

            // 50% master × 80% per-sound = 40% effective.
            engine.ActiveGameSoundForTests!.Volume.Should().BeApproximately(0.4f, 0.001f);
        }
        finally { engine.Shutdown(); }
    }

    [Fact]
    public async Task SetVolume_Mid_Play_Recomputes_Effective_Volume()
    {
        var (engine, library, id) = Build();
        try
        {
            library.UpdateSound(id, volume: 50);
            engine.Play(id, Path.Combine(_tempDir, "sounds", library.ActiveBoard.Sounds[0].File));
            await Task.Delay(100);
            // Starting effective = 1.0 * 0.5 = 0.5.
            engine.ActiveGameSoundForTests!.Volume.Should().BeApproximately(0.5f, 0.001f);

            // Drop master to 50 — effective should now be 0.5 * 0.5 = 0.25.
            engine.SetVolume(50);
            await Task.Delay(100);
            engine.ActiveGameSoundForTests!.Volume.Should().BeApproximately(0.25f, 0.001f);
        }
        finally { engine.Shutdown(); }
    }

    [Fact]
    public async Task Per_Sound_Volume_100_Equals_Master_Volume()
    {
        var (engine, library, id) = Build();
        try
        {
            // Default per-sound is 100, so effective should equal master.
            engine.SetVolume(70);
            await Task.Delay(50);
            engine.Play(id, Path.Combine(_tempDir, "sounds", library.ActiveBoard.Sounds[0].File));
            await Task.Delay(150);

            engine.ActiveGameSoundForTests!.Volume.Should().BeApproximately(0.7f, 0.001f);
        }
        finally { engine.Shutdown(); }
    }
}
