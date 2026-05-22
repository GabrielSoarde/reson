using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class SoundCacheTests
{
    private static CachedSound MakeCached(int sizeBytes = 16)
        => new(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[sizeBytes]);

    [Fact]
    public void Get_Decodes_On_First_Call()
    {
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode("x.mp3")).Returns(MakeCached());
        var cache = new SoundCache(decoder.Object, capacity: 10);

        cache.Get("x.mp3").Should().NotBeNull();
        decoder.Verify(d => d.Decode("x.mp3"), Times.Once);
    }

    [Fact]
    public void Get_Cache_Hit_Does_Not_Redecode()
    {
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode("x.mp3")).Returns(MakeCached());
        var cache = new SoundCache(decoder.Object, capacity: 10);

        cache.Get("x.mp3");
        cache.Get("x.mp3");
        cache.Get("x.mp3");
        decoder.Verify(d => d.Decode("x.mp3"), Times.Once);
    }

    [Fact]
    public void Lru_Evicts_Least_Recently_Used()
    {
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>())).Returns(MakeCached());
        var cache = new SoundCache(decoder.Object, capacity: 2);

        cache.Get("a.mp3");
        cache.Get("b.mp3");
        cache.Get("a.mp3"); // a now MRU, b is LRU
        cache.Get("c.mp3"); // evicts b
        cache.Get("b.mp3"); // re-decode

        decoder.Verify(d => d.Decode("a.mp3"), Times.Once);
        decoder.Verify(d => d.Decode("b.mp3"), Times.Exactly(2));
        decoder.Verify(d => d.Decode("c.mp3"), Times.Once);
    }
}
