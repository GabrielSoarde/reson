using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundpad.Audio;

/// <summary>
/// Audio engine implementing the "Steam Soundpad" model: a single continuous
/// output mix that combines the user's microphone with whatever sound is
/// playing, sent to one virtual cable that Discord/Valorant pick as their
/// microphone.
///
/// Pipeline:
///   [mic capture] ─┐
///                  ├─► [game mixer] ─► WasapiOut (always-on) ─► game device
///   [active sound]─┤
///                  └─► [monitor mixer] ─► WasapiOut (on demand) ─► headphones
/// </summary>
public class PlaybackEngine
{
    /// <summary>Working format: 48 kHz stereo float, the industry-standard
    /// rate for VoIP virtual cables.</summary>
    public static readonly WaveFormat WorkingFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private readonly IWavePlayerFactory _factory;
    private readonly SoundCache _cache;
    private readonly IMicCapture _mic;
    private readonly BlockingCollection<AudioCommand> _queue = new();
    private Thread? _thread;

    // Long-lived game pipeline. Built when the engine has a game device and
    // tear down only on Shutdown or when the game device changes.
    private MixingSampleProvider? _gameMixer;
    private IWavePlayer? _gameOutput;
    private string? _gameDevice;

    // Monitor pipeline — built on demand when enabled + device set.
    private MixingSampleProvider? _monitorMixer;
    private IWavePlayer? _monitorOutput;
    private string? _monitorDevice;
    private bool _monitorEnabled;

    // Per-play state. _activeGameSound feeds the game mixer; _activeMonitorSound
    // (if any) feeds the monitor mixer with an independent read position.
    private SoundSampleProvider? _activeGameSound;
    private SoundSampleProvider? _activeMonitorSound;
    private long _playToken;
    private string? _nowPlaying;
    private string? _micDevice;
    private int _volume = 80;
    private int _latencyMs = 50;

    public event Action<string>? Playing;
    public event Action? Stopped;
    public event Action<bool>? MonitorChanged;
    public event Action<int>? VolumeChanged;
    public event Action<string?>? MonitorDeviceChanged;
    public event Action<Exception>? MicError;

    public PlaybackEngine(IWavePlayerFactory factory, SoundCache cache)
        : this(factory, cache, new WasapiMicCapture()) { }

    public PlaybackEngine(IWavePlayerFactory factory, SoundCache cache, IMicCapture mic)
    {
        _factory = factory;
        _cache = cache;
        _mic = mic;
        _mic.CaptureError += ex => MicError?.Invoke(ex);
    }

    public bool IsRunning => _thread is { IsAlive: true };
    public string? NowPlaying => _nowPlaying;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "audio-engine" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // Trigger pipeline build on the engine thread immediately, so that
        // mic passthrough begins even before the first Play.
        _queue.Add(new InitPipelineCommand());
    }

    public void Shutdown()
    {
        if (_thread is null) return;
        _queue.Add(new ShutdownCommand());
        _thread.Join();
        _thread = null;
    }

    public void Play(string id, string filePath) => Post(new PlayCommand(id, filePath));
    public void Stop() => Post(new StopCommand());
    public void SetVolume(int v) => Post(new VolumeCommand(v));
    public void SetMonitorEnabled(bool b) => Post(new SetMonitorEnabledCommand(b));
    public void SetMonitorDevice(string? d) => Post(new SetMonitorDeviceCommand(d));
    public void SetGameDevice(string? d) => Post(new SetGameDeviceCommand(d));
    public void SetMicDevice(string? d) => Post(new SetMicDeviceCommand(d));
    public void SetLatency(int ms) { _latencyMs = ms; }

    private void Post(AudioCommand c)
    {
        if (_queue.IsAddingCompleted) throw new InvalidOperationException("Engine shut down");
        _queue.Add(c);
    }

    private void Loop()
    {
        foreach (var cmd in _queue.GetConsumingEnumerable())
        {
            try { Dispatch(cmd); }
            catch (Exception ex) { Console.Error.WriteLine($"audio-engine: {ex}"); }
            if (cmd is ShutdownCommand)
            {
                TearDownAll();
                _queue.CompleteAdding();
                break;
            }
        }
    }

    private void Dispatch(AudioCommand cmd)
    {
        switch (cmd)
        {
            case InitPipelineCommand: EnsureGamePipeline(); EnsureMicRunning(); break;
            case PlayCommand p: HandlePlay(p); break;
            case StopCommand: HandleStop(); break;
            case VolumeCommand v: HandleVolume(v.Value); break;
            case SetMonitorEnabledCommand m: HandleSetMonitorEnabled(m.Enabled); break;
            case SetMonitorDeviceCommand md: HandleSetMonitorDevice(md.Device); break;
            case SetGameDeviceCommand gd: HandleSetGameDevice(gd.Device); break;
            case SetMicDeviceCommand sm: HandleSetMicDevice(sm.Device); break;
            case PlaybackEndedCommand pe: HandlePlaybackEnded(pe); break;
            case ShutdownCommand: /* handled by Loop */ break;
        }
    }

    // ─── pipeline lifecycle ──────────────────────────────────────────────

    private void EnsureGamePipeline()
    {
        if (_gameOutput is not null) return;
        if (string.IsNullOrEmpty(_gameDevice)) return;

        var mixer = new MixingSampleProvider(WorkingFormat)
        {
            ReadFully = true, // never let the output starve when the mic has no data
        };
        // Mic is always input #0 — added on the first build and never removed.
        mixer.AddMixerInput(_mic.Samples);
        _gameMixer = mixer;

        var player = _factory.Create(_gameDevice, _latencyMs);
        player.Init(mixer);
        player.Play();
        _gameOutput = player;
    }

    private void TearDownGamePipeline()
    {
        if (_gameOutput is not null)
        {
            try { _gameOutput.Stop(); } catch { }
            try { _gameOutput.Dispose(); } catch { }
            _gameOutput = null;
        }
        _gameMixer = null;
        _activeGameSound = null;
    }

    private void EnsureMonitorPipeline()
    {
        if (_monitorOutput is not null) return;
        if (!_monitorEnabled || string.IsNullOrEmpty(_monitorDevice)) return;

        var mixer = new MixingSampleProvider(WorkingFormat) { ReadFully = true };
        _monitorMixer = mixer;

        var player = _factory.Create(_monitorDevice, _latencyMs);
        player.Init(mixer);
        player.Play();
        _monitorOutput = player;
    }

    private void TearDownMonitorPipeline()
    {
        if (_monitorOutput is not null)
        {
            try { _monitorOutput.Stop(); } catch { }
            try { _monitorOutput.Dispose(); } catch { }
            _monitorOutput = null;
        }
        _monitorMixer = null;
        _activeMonitorSound = null;
    }

    private void EnsureMicRunning()
    {
        if (_mic.IsRunning) return;
        try
        {
            _mic.Start(_micDevice, WorkingFormat);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"audio-engine: mic start failed: {ex.Message}");
            MicError?.Invoke(ex);
        }
    }

    private void TearDownAll()
    {
        TearDownGamePipeline();
        TearDownMonitorPipeline();
        try { _mic.Stop(); } catch { }
        try { _mic.Dispose(); } catch { }
    }

    // ─── command handlers ────────────────────────────────────────────────

    private void HandleSetGameDevice(string? device)
    {
        if (device == _gameDevice) return;
        // Device changed: tear down and rebuild on next play (or now if we
        // already have one).
        TearDownGamePipeline();
        _gameDevice = device;
        EnsureGamePipeline();
    }

    private void HandleSetMicDevice(string? device)
    {
        _micDevice = device;
        // Restart capture if running; otherwise the next EnsureMicRunning
        // will use the new device.
        if (_mic.IsRunning)
        {
            try { _mic.Stop(); } catch { }
        }
        EnsureMicRunning();
    }

    private void HandlePlay(PlayCommand cmd)
    {
        if (string.IsNullOrEmpty(_gameDevice))
        {
            Console.Error.WriteLine("PlayCommand: no game device configured");
            return;
        }

        EnsureGamePipeline();
        EnsureMicRunning();
        EnsureMonitorPipeline();

        // Remove any previously-active sounds from their mixers. Keeping the
        // mic input in place is what enables continuous passthrough.
        DetachActiveSounds();

        var token = Interlocked.Increment(ref _playToken);
        var cached = _cache.Get(cmd.FilePath);
        var volume = _volume / 100f;

        var gameSound = new SoundSampleProvider(cached, WorkingFormat, volume);
        gameSound.Finished += _ => _queue.Add(new PlaybackEndedCommand(token, IsGameStream: true));
        _gameMixer!.AddMixerInput((ISampleProvider)gameSound);
        _activeGameSound = gameSound;

        if (_monitorOutput is not null && _monitorMixer is not null)
        {
            var monitorSound = new SoundSampleProvider(cached, WorkingFormat, volume);
            monitorSound.Finished += _ => _queue.Add(new PlaybackEndedCommand(token, IsGameStream: false));
            _monitorMixer.AddMixerInput((ISampleProvider)monitorSound);
            _activeMonitorSound = monitorSound;
        }

        _nowPlaying = cmd.SoundId;
        Playing?.Invoke(cmd.SoundId);
    }

    private void HandleStop()
    {
        DetachActiveSounds();
        _nowPlaying = null;
        Stopped?.Invoke();
    }

    private void HandleVolume(int v)
    {
        _volume = Math.Clamp(v, 0, 100);
        var f = _volume / 100f;
        if (_activeGameSound is not null) _activeGameSound.Volume = f;
        if (_activeMonitorSound is not null) _activeMonitorSound.Volume = f;
        VolumeChanged?.Invoke(_volume);
    }

    private void HandleSetMonitorEnabled(bool b)
    {
        _monitorEnabled = b;
        if (!b) TearDownMonitorPipeline();
        // We deliberately do NOT EnsureMonitorPipeline here — we apply at
        // next Play, preserving the existing semantic that toggling monitor
        // mid-playback doesn't affect the current sound.
        MonitorChanged?.Invoke(b);
    }

    private void HandleSetMonitorDevice(string? d)
    {
        _monitorDevice = d;
        // Tear down so the next Play picks up the new device. Apply-on-next-play
        // semantics match the legacy engine.
        if (_monitorOutput is not null) TearDownMonitorPipeline();
        MonitorDeviceChanged?.Invoke(d);
    }

    private void HandlePlaybackEnded(PlaybackEndedCommand cmd)
    {
        if (cmd.PlayToken != _playToken) return; // stale (a newer Play already started)
        if (!cmd.IsGameStream) return; // monitor end is not authoritative
        DetachActiveSounds();
        _nowPlaying = null;
        Stopped?.Invoke();
    }

    private void DetachActiveSounds()
    {
        if (_activeGameSound is not null)
        {
            try { _gameMixer?.RemoveMixerInput((ISampleProvider)_activeGameSound); } catch { }
            _activeGameSound = null;
        }
        if (_activeMonitorSound is not null)
        {
            try { _monitorMixer?.RemoveMixerInput((ISampleProvider)_activeMonitorSound); } catch { }
            _activeMonitorSound = null;
        }
    }

    // ─── test affordances ────────────────────────────────────────────────

    /// <summary>
    /// For tests: the current game mixer (after EnsureGamePipeline runs).
    /// Null until a game device is set. Includes the mic input at index 0.
    /// </summary>
    internal MixingSampleProvider? GameMixerForTests => _gameMixer;

    /// <summary>For tests: the current monitor mixer, null until enabled.</summary>
    internal MixingSampleProvider? MonitorMixerForTests => _monitorMixer;

    /// <summary>For tests: the current active sound on the game mixer.</summary>
    internal SoundSampleProvider? ActiveGameSoundForTests => _activeGameSound;

    /// <summary>For tests: the current active sound on the monitor mixer.</summary>
    internal SoundSampleProvider? ActiveMonitorSoundForTests => _activeMonitorSound;

    /// <summary>
    /// For tests: simulate the active game (or monitor) sound finishing.
    /// Replaces the old "drive a real WasapiOut and wait for natural EOF"
    /// pattern, which is non-deterministic in unit tests.
    /// </summary>
    internal void SimulateActiveSoundEnded(bool isGameStream)
    {
        _queue.Add(new PlaybackEndedCommand(_playToken, isGameStream));
    }

    /// <summary>For tests: the long-lived game output player.</summary>
    internal IWavePlayer? GameOutputForTests => _gameOutput;

    /// <summary>For tests: the on-demand monitor output player.</summary>
    internal IWavePlayer? MonitorOutputForTests => _monitorOutput;
}
