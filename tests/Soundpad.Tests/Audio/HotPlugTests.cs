using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

/// <summary>
/// Covers the IMMNotificationClient bridge into the engine. The actual
/// IMMNotificationClient implementation is too OS-bound to unit-test; here
/// we feed events through <see cref="PlaybackEngine.NotifyDeviceUnplugged"/>
/// directly (which is what <see cref="DeviceChangeNotifier"/> would do
/// from a COM callback).
/// </summary>
public class HotPlugTests
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();
    private readonly FakeMicCapture _mic = new();

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string id, int lat) => { var p = new FakeWavePlayer(id); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new byte[8192]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache, _mic);
        return engine;
    }

    [Fact]
    public async Task Unplugging_Game_Device_Tears_Down_Game_Pipeline_Without_Crash()
    {
        var engine = Build();
        engine.SetGameDevice("DEV1");
        engine.Start();
        await Task.Delay(100);
        engine.GameOutputForTests.Should().NotBeNull();
        engine.GameOutputForTests!.PlaybackState.Should().Be(PlaybackState.Playing);

        // Simulate the COM-thread callback marshaled by the notifier.
        engine.NotifyDeviceUnplugged("DEV1");
        await Task.Delay(100);

        engine.GameOutputForTests.Should().BeNull();
        // The disposed player is still in our list — confirm it was Stop/Disposed.
        _players.Should().HaveCount(1);
        _players[0].Disposed.Should().BeTrue();
        engine.Shutdown();
    }

    [Fact]
    public async Task Re_Plugging_Game_Device_Rebuilds_Pipeline()
    {
        var engine = Build();
        engine.SetGameDevice("DEV1");
        engine.Start();
        await Task.Delay(100);

        engine.NotifyDeviceUnplugged("DEV1");
        await Task.Delay(100);
        engine.GameOutputForTests.Should().BeNull();

        engine.NotifyDevicePlugged("DEV1");
        await Task.Delay(100);

        engine.GameOutputForTests.Should().NotBeNull();
        engine.GameOutputForTests!.PlaybackState.Should().Be(PlaybackState.Playing);
        // Now there are two FakeWavePlayer instances total — one disposed, one live.
        _players.Should().HaveCount(2);
        _players[1].DeviceName.Should().Be("DEV1");
        engine.Shutdown();
    }

    [Fact]
    public async Task Unplugging_Unrelated_Device_Is_NoOp()
    {
        var engine = Build();
        engine.SetGameDevice("DEV1");
        engine.Start();
        await Task.Delay(100);

        engine.NotifyDeviceUnplugged("SOME_OTHER_DEVICE");
        await Task.Delay(50);

        engine.GameOutputForTests.Should().NotBeNull();
        engine.GameOutputForTests!.PlaybackState.Should().Be(PlaybackState.Playing);
        engine.Shutdown();
    }

    [Fact]
    public async Task Unplugging_Monitor_Device_Leaves_Game_Running()
    {
        var engine = Build();
        engine.SetGameDevice("GAME");
        engine.SetMonitorDevice("MON");
        engine.SetMonitorEnabled(true);
        engine.Start();
        engine.Play("a", "a.mp3");
        await Task.Delay(150);

        engine.GameOutputForTests.Should().NotBeNull();
        engine.MonitorOutputForTests.Should().NotBeNull();

        engine.NotifyDeviceUnplugged("MON");
        await Task.Delay(100);

        engine.MonitorOutputForTests.Should().BeNull();
        engine.GameOutputForTests.Should().NotBeNull();
        engine.GameOutputForTests!.PlaybackState.Should().Be(PlaybackState.Playing);
        engine.Shutdown();
    }

    [Fact]
    public async Task Re_Plugging_Monitor_Rebuilds_If_Enabled()
    {
        var engine = Build();
        engine.SetGameDevice("GAME");
        engine.SetMonitorDevice("MON");
        engine.SetMonitorEnabled(true);
        engine.Start();
        engine.Play("a", "a.mp3");
        await Task.Delay(150);
        engine.MonitorOutputForTests.Should().NotBeNull();

        engine.NotifyDeviceUnplugged("MON");
        await Task.Delay(100);
        engine.MonitorOutputForTests.Should().BeNull();

        engine.NotifyDevicePlugged("MON");
        await Task.Delay(100);

        engine.MonitorOutputForTests.Should().NotBeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Re_Plugging_Monitor_NoOp_If_Monitor_Disabled()
    {
        var engine = Build();
        engine.SetGameDevice("GAME");
        engine.SetMonitorDevice("MON");
        // monitor explicitly OFF
        engine.Start();
        await Task.Delay(100);
        engine.MonitorOutputForTests.Should().BeNull();

        engine.NotifyDevicePlugged("MON");
        await Task.Delay(50);
        engine.MonitorOutputForTests.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Unplugging_Mic_Device_Stops_Capture_Without_Crash()
    {
        var engine = Build();
        engine.SetGameDevice("GAME");
        engine.SetMicDevice("MIC");
        engine.Start();
        await Task.Delay(100);
        _mic.IsRunning.Should().BeTrue();
        _mic.LastStartedDevice.Should().Be("MIC");

        engine.NotifyDeviceUnplugged("MIC");
        await Task.Delay(100);

        _mic.IsRunning.Should().BeFalse();
        engine.Shutdown();
    }

    [Fact]
    public async Task Re_Plugging_Mic_Device_Restarts_Capture()
    {
        var engine = Build();
        engine.SetGameDevice("GAME");
        engine.SetMicDevice("MIC");
        engine.Start();
        await Task.Delay(100);

        engine.NotifyDeviceUnplugged("MIC");
        await Task.Delay(100);
        _mic.IsRunning.Should().BeFalse();

        engine.NotifyDevicePlugged("MIC");
        await Task.Delay(100);

        _mic.IsRunning.Should().BeTrue();
        _mic.LastStartedDevice.Should().Be("MIC");
        engine.Shutdown();
    }

    [Fact]
    public void NotifyDevice_After_Shutdown_Does_Not_Throw()
    {
        var engine = Build();
        engine.SetGameDevice("G");
        engine.Start();
        engine.Shutdown();

        // After shutdown the queue is closed — these should swallow the
        // race (they're called from COM threads we don't control) rather
        // than crash the host process.
        var act1 = () => engine.NotifyDeviceUnplugged("G");
        var act2 = () => engine.NotifyDevicePlugged("G");
        act1.Should().NotThrow();
        act2.Should().NotThrow();
    }
}
