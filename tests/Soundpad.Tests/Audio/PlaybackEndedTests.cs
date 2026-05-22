using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class PlaybackEndedTests
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
        engine.SetGameDevice("Game");
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Natural_End_Of_Game_Stream_Emits_Stopped()
    {
        var engine = Build();
        bool stopped = false;
        engine.Stopped += () => stopped = true;
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        _players[0].RaiseStopped();
        await Task.Delay(100);
        stopped.Should().BeTrue();
        engine.NowPlaying.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Monitor_End_Alone_Does_Not_Stop()
    {
        var engine = Build();
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        bool stopped = false;
        engine.Stopped += () => stopped = true;
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        var monitor = _players.Single(p => p.DeviceName == "Headphones");
        monitor.RaiseStopped();
        await Task.Delay(100);
        stopped.Should().BeFalse();
        engine.NowPlaying.Should().Be("a");
        engine.Shutdown();
    }

    [Fact]
    public async Task Stale_Token_End_Of_A_Does_Not_Clear_B()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Play("b", "b.mp3");
        await Task.Delay(50);
        // First player got disposed when b was played. Even if its handler somehow fired:
        _players[0].RaiseStopped(); // stale token
        await Task.Delay(100);
        engine.NowPlaying.Should().Be("b");
        engine.Shutdown();
    }
}
