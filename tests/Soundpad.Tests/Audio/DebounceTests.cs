using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class DebounceTests
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
        engine.SetGameDevice("Game");
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Rapid_Repeat_Same_Sound_Plays_Only_Once()
    {
        var engine = Build();
        var playingCount = 0;
        engine.Playing += _ => Interlocked.Increment(ref playingCount);
        // Wait for engine to warm up.
        await Task.Delay(50);

        // Fire 10 PlayCommands for the same sound id in tight succession.
        for (int i = 0; i < 10; i++) engine.Play("a", "a.mp3");
        await Task.Delay(150);

        // Only the first should be accepted — the next 9 land inside the
        // 80 ms debounce window and are dropped.
        playingCount.Should().Be(1);
        engine.Shutdown();
    }

    [Fact]
    public async Task Different_Sounds_Back_To_Back_Are_Not_Debounced()
    {
        var engine = Build();
        var playingIds = new List<string>();
        engine.Playing += id => { lock (playingIds) playingIds.Add(id); };
        await Task.Delay(50);

        engine.Play("a", "a.mp3");
        engine.Play("b", "b.mp3");
        engine.Play("c", "c.mp3");
        await Task.Delay(150);

        playingIds.Should().BeEquivalentTo(new[] { "a", "b", "c" });
        engine.Shutdown();
    }

    [Fact]
    public async Task Same_Sound_Replayable_After_Debounce_Window_Elapses()
    {
        var engine = Build();
        var playingCount = 0;
        engine.Playing += _ => Interlocked.Increment(ref playingCount);
        await Task.Delay(50);

        engine.Play("a", "a.mp3");
        await Task.Delay(150); // wait past the 80 ms debounce window
        engine.Play("a", "a.mp3");
        await Task.Delay(150);

        playingCount.Should().Be(2);
        engine.Shutdown();
    }
}
