using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class PlayCommandTests
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[4096]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache);
        engine.SetGameDevice("VoiceMeeter Input");
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Play_Single_Device_Creates_One_Player()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
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
    public async Task Play_Then_Play_Stops_Previous_First()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Play("b", "b.mp3");
        await Task.Delay(100);
        _players[0].Disposed.Should().BeTrue();
        _players[1].PlaybackState.Should().Be(PlaybackState.Playing);
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
}
