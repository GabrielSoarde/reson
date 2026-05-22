using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class VolumeAndMonitorDeviceTests
{
    private PlaybackEngine Build(out List<FakeWavePlayer> players)
    {
        var p = new List<FakeWavePlayer>();
        var factory = new Mock<IWavePlayerFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string n, int lat) => { var x = new FakeWavePlayer(n); p.Add(x); return x; });
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[1024]));
        var cache = new SoundCache(decoder.Object, 50);
        var engine = new PlaybackEngine(factory.Object, cache);
        engine.SetGameDevice("G");
        engine.Start();
        players = p;
        return engine;
    }

    [Fact]
    public async Task SetVolume_During_Playback_Emits_Event()
    {
        var engine = Build(out var _);
        int? heard = null;
        engine.VolumeChanged += v => heard = v;
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.SetVolume(42);
        await Task.Delay(50);
        heard.Should().Be(42);
        engine.Shutdown();
    }

    [Fact]
    public async Task SetMonitorDevice_Emits_Event()
    {
        var engine = Build(out var _);
        string? heard = "unchanged";
        engine.MonitorDeviceChanged += d => heard = d;
        engine.SetMonitorDevice("Foo");
        await Task.Delay(50);
        heard.Should().Be("Foo");
        engine.Shutdown();
    }

    [Fact]
    public async Task SetMonitorEnabled_Emits_Event()
    {
        var engine = Build(out var _);
        bool? heard = null;
        engine.MonitorChanged += b => heard = b;
        engine.SetMonitorEnabled(true);
        await Task.Delay(50);
        heard.Should().BeTrue();
        engine.Shutdown();
    }
}
