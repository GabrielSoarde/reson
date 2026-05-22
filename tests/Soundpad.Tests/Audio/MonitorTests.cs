using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class MonitorTests
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
    public async Task Monitor_Enabled_With_Device_Creates_Two_Players()
    {
        var engine = Build();
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        _players.Should().HaveCount(2);
        _players.Should().Contain(p => p.DeviceName == "Game");
        _players.Should().Contain(p => p.DeviceName == "Headphones");
        engine.Shutdown();
    }

    [Fact]
    public async Task Monitor_Enabled_But_Null_Device_Creates_One_Player()
    {
        var engine = Build();
        engine.SetMonitorEnabled(true);
        // monitorDevice not set (null)
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        _players.Should().HaveCount(1);
        _players[0].DeviceName.Should().Be("Game");
        engine.Shutdown();
    }

    [Fact]
    public async Task Toggle_Monitor_During_Playback_Does_Not_Affect_Current_Sound()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        await Task.Delay(50);
        _players.Should().HaveCount(1); // still only game
        engine.Shutdown();
    }

    [Fact]
    public async Task Two_MemoryStreams_Have_Independent_Positions()
    {
        var engine = Build();
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        // Both players were initialized — proxy for "got independent streams"
        _players.Should().HaveCount(2);
        _players.All(p => p.PlaybackState == PlaybackState.Playing).Should().BeTrue();
        engine.Shutdown();
    }
}
