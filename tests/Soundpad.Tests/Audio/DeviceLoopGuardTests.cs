using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class DeviceLoopGuardTests
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new byte[8192]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache, new FakeMicCapture());
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Engine_Refuses_To_Build_Monitor_When_Same_As_Game_Device()
    {
        var engine = Build();
        engine.SetGameDevice("foo");
        engine.SetMonitorDevice("foo");
        engine.SetMonitorEnabled(true);
        engine.Play("a", "a.mp3");
        await Task.Delay(150);

        // Only the game output should exist.
        _players.Should().HaveCount(1);
        _players[0].DeviceName.Should().Be("foo");
        engine.MonitorOutputForTests.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Engine_Refuses_To_Build_Monitor_When_Same_As_Game_Device_CaseInsensitive()
    {
        var engine = Build();
        engine.SetGameDevice("VoiceMeeter Input");
        engine.SetMonitorDevice("voicemeeter input");
        engine.SetMonitorEnabled(true);
        engine.Play("a", "a.mp3");
        await Task.Delay(150);

        _players.Should().HaveCount(1);
        engine.MonitorOutputForTests.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Engine_Builds_Monitor_When_Devices_Differ()
    {
        var engine = Build();
        engine.SetGameDevice("foo");
        engine.SetMonitorDevice("bar");
        engine.SetMonitorEnabled(true);
        engine.Play("a", "a.mp3");
        await Task.Delay(150);

        _players.Should().HaveCount(2);
        engine.MonitorOutputForTests.Should().NotBeNull();
        engine.Shutdown();
    }
}
