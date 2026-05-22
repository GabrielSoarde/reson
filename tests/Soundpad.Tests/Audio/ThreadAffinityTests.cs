using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class ThreadAffinityTests
{
    [Fact]
    public async Task All_Player_Lifecycle_Occurs_On_AudioEngine_Thread()
    {
        var factory = new Mock<IWavePlayerFactory>();
        var players = new List<FakeWavePlayer>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string n, int lat) => { var p = new FakeWavePlayer(n); players.Add(p); return p; });
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[1024]));
        var cache = new SoundCache(decoder.Object, 50);
        var engine = new PlaybackEngine(factory.Object, cache);
        engine.SetGameDevice("G");
        engine.Start();

        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Stop();
        await Task.Delay(50);

        var caller = Thread.CurrentThread.ManagedThreadId;
        var p = players[0];
        p.ManagedThreadIdAtInit.Should().NotBe(caller);
        p.ManagedThreadIdAtPlay.Should().Be(p.ManagedThreadIdAtInit);
        p.ManagedThreadIdAtDispose.Should().Be(p.ManagedThreadIdAtInit);

        engine.Shutdown();
    }
}
