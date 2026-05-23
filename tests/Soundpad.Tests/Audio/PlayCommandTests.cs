using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;
using Soundpad.Sound;

namespace Soundpad.Tests.Audio;

public class PlayCommandTests : IDisposable
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();
    private string? _statTempDir;

    public void Dispose()
    {
        if (_statTempDir is not null)
        {
            try { Directory.Delete(_statTempDir, recursive: true); } catch { }
        }
    }

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[4096]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache, new FakeMicCapture());
        engine.SetGameDevice("VoiceMeeter Input");
        engine.Start();
        return engine;
    }

    /// <summary>
    /// Build an engine wired to a real SoundLibrary backed by a temp dir.
    /// Lets us assert that HandlePlay's RecordPlay call actually updates
    /// PlayCount on the library. Disposed via the class IDisposable.
    /// </summary>
    private (PlaybackEngine Engine, SoundLibrary Library, string SoundId) BuildWithLibrary()
    {
        _statTempDir = Path.Combine(Path.GetTempPath(), "soundpad-engine-stats-" + Guid.NewGuid());
        Directory.CreateDirectory(_statTempDir);
        Directory.CreateDirectory(Path.Combine(_statTempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_statTempDir, "sounds", "a.mp3"), new byte[10]);
        var library = new SoundLibrary(new SoundLibraryOptions(_statTempDir));
        library.Load();
        library.AutoScan();
        var id = library.ActiveBoard.Sounds.Single().Id;

        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[4096]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache, new FakeMicCapture(), library);
        engine.SetGameDevice("VoiceMeeter Input");
        engine.Start();
        return (engine, library, id);
    }

    [Fact]
    public async Task Play_Single_Device_Creates_One_Player()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        // New model: exactly one long-lived game player is built when the
        // engine starts (or when SetGameDevice runs). Playing a sound mixes
        // it into that player — does NOT create a new one.
        _players.Should().HaveCount(1);
        _players[0].DeviceName.Should().Be("VoiceMeeter Input");
        _players[0].PlaybackState.Should().Be(PlaybackState.Playing);
        engine.NowPlaying.Should().Be("a");
        engine.Shutdown();
    }

    [Fact]
    public async Task Play_Emits_Playing_Event()
    {
        var engine = Build();
        string? heard = null;
        engine.Playing += id => heard = id;
        engine.Play("ulala", "x.mp3");
        await Task.Delay(100);
        heard.Should().Be("ulala");
        engine.Shutdown();
    }

    [Fact]
    public async Task Play_Then_Play_Replaces_Active_Sound_On_Same_Player()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Play("b", "b.mp3");
        await Task.Delay(100);
        // The game output is long-lived now. Playing "b" should NOT dispose
        // the player; it should swap the active sound inside the mixer.
        _players.Should().HaveCount(1);
        _players[0].Disposed.Should().BeFalse();
        _players[0].PlaybackState.Should().Be(PlaybackState.Playing);
        engine.NowPlaying.Should().Be("b");
        engine.Shutdown();
    }

    [Fact]
    public async Task Player_Created_On_AudioEngine_Thread()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        _players[0].ManagedThreadIdAtInit.Should().Be(_players[0].ManagedThreadIdAtPlay);
        engine.Shutdown();
    }

    [Fact]
    public async Task Play_Records_Usage_Stat()
    {
        var (engine, library, id) = BuildWithLibrary();
        var soundsDir = Path.Combine(_statTempDir!, "sounds");
        engine.Play(id, Path.Combine(soundsDir, library.ActiveBoard.Sounds.Single().File));
        await Task.Delay(150); // drain the audio engine queue past the debounce check

        var entry = library.ActiveBoard.Sounds.Single(s => s.Id == id);
        entry.PlayCount.Should().Be(1);
        entry.LastPlayedAt.Should().NotBeNull();
        engine.Shutdown();
    }
}
