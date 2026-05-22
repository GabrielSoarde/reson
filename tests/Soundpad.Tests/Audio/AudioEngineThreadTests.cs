using FluentAssertions;
using Moq;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class AudioEngineThreadTests
{
    [Fact]
    public void Shutdown_Stops_The_Thread()
    {
        var factory = new Mock<IWavePlayerFactory>();
        var cache = new SoundCache(new Mock<ISoundDecoder>().Object, capacity: 10);
        var engine = new PlaybackEngine(factory.Object, cache, new FakeMicCapture());
        engine.Start();
        engine.Shutdown();
        engine.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Posting_Command_After_Shutdown_Throws()
    {
        var factory = new Mock<IWavePlayerFactory>();
        var cache = new SoundCache(new Mock<ISoundDecoder>().Object, capacity: 10);
        var engine = new PlaybackEngine(factory.Object, cache, new FakeMicCapture());
        engine.Start();
        engine.Shutdown();
        Assert.Throws<InvalidOperationException>(() => engine.Play("x", "x.mp3"));
    }
}
