using FluentAssertions;
using Moq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

/// <summary>
/// Tests for the new "Steam Soundpad" continuous-mix architecture: mic
/// passthrough + active sound mixed into a long-lived game output.
/// </summary>
public class MicMixingTests
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();
    private readonly FakeMicCapture _mic = new();

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new byte[8192]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache, _mic);
        engine.SetGameDevice("Game");
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Game_Output_Starts_On_Engine_Start_Even_Without_Play()
    {
        var engine = Build();
        await Task.Delay(100);
        // The "always-on" game pipeline must come up as soon as the engine
        // has a game device, before any Play arrives — that's what makes
        // mic passthrough continuous.
        _players.Should().HaveCount(1);
        _players[0].PlaybackState.Should().Be(PlaybackState.Playing);
        engine.Shutdown();
    }

    [Fact]
    public async Task Mic_Starts_On_Engine_Start()
    {
        var engine = Build();
        await Task.Delay(100);
        _mic.IsRunning.Should().BeTrue();
        _mic.StartCalls.Should().BeGreaterThan(0);
        engine.Shutdown();
    }

    [Fact]
    public async Task Mic_Configured_With_48k_Stereo_Float()
    {
        var engine = Build();
        await Task.Delay(100);
        _mic.OutputFormat.SampleRate.Should().Be(48000);
        _mic.OutputFormat.Channels.Should().Be(2);
        _mic.OutputFormat.Encoding.Should().Be(WaveFormatEncoding.IeeeFloat);
        engine.Shutdown();
    }

    [Fact]
    public async Task Play_Adds_Sound_To_Game_Mixer_Without_Removing_Mic()
    {
        var engine = Build();
        await Task.Delay(50);
        engine.GameMixerForTests!.MixerInputs.Should().HaveCount(1); // mic
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        engine.GameMixerForTests!.MixerInputs.Should().HaveCount(2); // mic + sound
        engine.Shutdown();
    }

    [Fact]
    public async Task Stop_Removes_Sound_From_Mixer_But_Keeps_Mic()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        engine.GameMixerForTests!.MixerInputs.Should().HaveCount(2);
        engine.Stop();
        await Task.Delay(100);
        engine.GameMixerForTests!.MixerInputs.Should().HaveCount(1); // mic remains
        engine.NowPlaying.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Game_Player_Reused_Across_Multiple_Plays()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Play("b", "b.mp3");
        await Task.Delay(50);
        engine.Play("c", "c.mp3");
        await Task.Delay(50);
        // Only one game player ever instantiated — the WasapiOut is long-lived.
        _players.Where(p => p.DeviceName == "Game").Should().HaveCount(1);
        _players[0].Disposed.Should().BeFalse();
        engine.Shutdown();
    }

    [Fact]
    public async Task SetMicDevice_Restarts_Capture_With_New_Device()
    {
        var engine = Build();
        await Task.Delay(50);
        _mic.LastStartedDevice.Should().BeNull(); // default
        engine.SetMicDevice("Headset Mic");
        await Task.Delay(50);
        _mic.LastStartedDevice.Should().Be("Headset Mic");
        _mic.StartCalls.Should().BeGreaterOrEqualTo(2);
        engine.Shutdown();
    }

    [Fact]
    public async Task Mic_Stopped_On_Shutdown()
    {
        var engine = Build();
        await Task.Delay(50);
        engine.Shutdown();
        _mic.IsRunning.Should().BeFalse();
        _mic.StopCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task MicError_Event_Forwards_Capture_Errors()
    {
        var engine = Build();
        Exception? heard = null;
        engine.MicError += ex => heard = ex;
        await Task.Delay(50);
        _mic.RaiseCaptureError(new InvalidOperationException("mic unplugged"));
        await Task.Delay(50);
        heard.Should().NotBeNull();
        heard!.Message.Should().Be("mic unplugged");
        engine.Shutdown();
    }

    [Fact]
    public async Task Game_Mixer_Sums_Mic_And_Sound_Samples()
    {
        // Pre-seed the mic with a known constant signal of 0.25.
        var micFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _mic.Start(null, micFormat);
        _mic.SetSamples(new ConstSampleProvider(micFormat, 0.25f));

        var engine = Build();
        // Override the generic decoder setup with a specific PCM for "const.wav"
        // — must happen AFTER Build() because Build()'s It.IsAny<> setup would
        // otherwise replace the specific one (Moq's most-recent-setup-wins).
        OverrideDecodeWithConstantSamples("const.wav", value: 0.5f);
        await Task.Delay(50);
        engine.Play("c", "const.wav");
        await Task.Delay(100);

        // Read directly from the engine's game mixer. With the active sound
        // at full volume (default 80%) plus the mic contribution, samples
        // should be 0.25 + 0.5 * 0.8 = 0.65.
        var buf = new float[256];
        var n = engine.GameMixerForTests!.Read(buf, 0, buf.Length);
        n.Should().Be(buf.Length);
        buf.Take(64).Should().AllSatisfy(s => s.Should().BeApproximately(0.65f, 0.01f));
        engine.Shutdown();
    }

    [Fact]
    public async Task Volume_Adjusts_Sound_Component_Only_Not_Mic()
    {
        var micFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _mic.Start(null, micFormat);
        _mic.SetSamples(new ConstSampleProvider(micFormat, 0.25f));

        var engine = Build();
        OverrideDecodeWithConstantSamples("c.wav", value: 0.5f);
        await Task.Delay(50);
        engine.SetVolume(0); // sound muted
        engine.Play("c", "c.wav");
        await Task.Delay(100);

        var buf = new float[256];
        engine.GameMixerForTests!.Read(buf, 0, buf.Length);
        // Mic still contributes 0.25, sound is muted: total = 0.25.
        buf.Take(64).Should().AllSatisfy(s => s.Should().BeApproximately(0.25f, 0.01f));
        engine.Shutdown();
    }

    private void OverrideDecodeWithConstantSamples(string filename, float value)
    {
        var samples = 48000 * 2; // one second of stereo
        var pcm = new byte[samples * sizeof(float)];
        for (int i = 0; i < samples; i++)
            BitConverter.GetBytes(value).CopyTo(pcm, i * sizeof(float));
        _decoder.Setup(d => d.Decode(filename))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), pcm));
    }

    /// <summary>Sample provider that emits a constant value on every channel/sample.</summary>
    private sealed class ConstSampleProvider : ISampleProvider
    {
        private readonly float _value;
        public ConstSampleProvider(WaveFormat fmt, float value) { WaveFormat = fmt; _value = value; }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++) buffer[offset + i] = _value;
            return count;
        }
    }
}
