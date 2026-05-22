using System.Collections.Concurrent;
using NAudio.Wave;

namespace Soundpad.Audio;

public class PlaybackEngine
{
    private readonly IWavePlayerFactory _factory;
    private readonly SoundCache _cache;
    private readonly BlockingCollection<AudioCommand> _queue = new();
    private Thread? _thread;
    private long _playToken;
    private IWavePlayer? _gamePlayer;
    private IWavePlayer? _monitorPlayer;
    private RawSourceWaveStream? _gameStream;
    private RawSourceWaveStream? _monitorStream;
    private MemoryStream? _gameMs;
    private MemoryStream? _monitorMs;
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _gameVol;
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _monitorVol;
    private string? _gameDevice;
    private string? _monitorDevice;
    private bool _monitorEnabled;
    private int _volume = 80;
    private int _latencyMs = 50;
    private string? _nowPlaying;

    public event Action<string>? Playing;
    public event Action? Stopped;
    public event Action<bool>? MonitorChanged;
    public event Action<int>? VolumeChanged;
    public event Action<string?>? MonitorDeviceChanged;

    public PlaybackEngine(IWavePlayerFactory factory, SoundCache cache)
    {
        _factory = factory; _cache = cache;
    }

    public bool IsRunning => _thread is { IsAlive: true };
    public string? NowPlaying => _nowPlaying;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "audio-engine" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
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
                DisposeAllPlayers();
                _queue.CompleteAdding();
                break;
            }
        }
    }

    private void Dispatch(AudioCommand cmd)
    {
        switch (cmd)
        {
            case PlayCommand p: HandlePlay(p); break;
            case StopCommand: HandleStop(); break;
            case VolumeCommand v: HandleVolume(v.Value); break;
            case SetMonitorEnabledCommand m: HandleSetMonitorEnabled(m.Enabled); break;
            case SetMonitorDeviceCommand md: HandleSetMonitorDevice(md.Device); break;
            case SetGameDeviceCommand gd: _gameDevice = gd.Device; break;
            case PlaybackEndedCommand pe: HandlePlaybackEnded(pe); break;
            case ShutdownCommand: /* handled by Loop */ break;
        }
    }

    private void HandlePlay(PlayCommand cmd)
    {
        if (string.IsNullOrEmpty(_gameDevice))
        {
            Console.Error.WriteLine("PlayCommand: no game device configured");
            return;
        }
        DisposeAllPlayers();
        var token = Interlocked.Increment(ref _playToken);
        var cached = _cache.Get(cmd.FilePath);

        (_gamePlayer, _gameStream, _gameMs, _gameVol) = OpenStream(cached, _gameDevice);
        _gamePlayer!.PlaybackStopped += (s, e) => _queue.Add(new PlaybackEndedCommand(token, IsGameStream: true));
        _gamePlayer.Init(_gameVol);
        _gamePlayer.Play();

        if (_monitorEnabled && !string.IsNullOrEmpty(_monitorDevice))
        {
            (_monitorPlayer, _monitorStream, _monitorMs, _monitorVol) = OpenStream(cached, _monitorDevice);
            _monitorPlayer!.PlaybackStopped += (s, e) => _queue.Add(new PlaybackEndedCommand(token, IsGameStream: false));
            _monitorPlayer.Init(_monitorVol);
            _monitorPlayer.Play();
        }

        _nowPlaying = cmd.SoundId;
        Playing?.Invoke(cmd.SoundId);
    }

    private (IWavePlayer Player, RawSourceWaveStream Stream, MemoryStream Ms, NAudio.Wave.SampleProviders.VolumeSampleProvider Vol)
        OpenStream(CachedSound cached, string deviceName)
    {
        var ms = new MemoryStream(cached.PcmBytes, writable: false);
        var raw = new RawSourceWaveStream(ms, cached.Format);
        var vol = new NAudio.Wave.SampleProviders.VolumeSampleProvider(raw.ToSampleProvider()) { Volume = _volume / 100f };
        var player = _factory.Create(deviceName, _latencyMs);
        return (player, raw, ms, vol);
    }

    private void HandleStop()
    {
        DisposeAllPlayers();
        _nowPlaying = null;
        Stopped?.Invoke();
    }

    private void HandleVolume(int v)
    {
        _volume = Math.Clamp(v, 0, 100);
        if (_gameVol is not null) _gameVol.Volume = _volume / 100f;
        if (_monitorVol is not null) _monitorVol.Volume = _volume / 100f;
        VolumeChanged?.Invoke(_volume);
    }

    private void HandleSetMonitorEnabled(bool b)
    {
        _monitorEnabled = b;
        MonitorChanged?.Invoke(b);
    }

    private void HandleSetMonitorDevice(string? d)
    {
        _monitorDevice = d;
        MonitorDeviceChanged?.Invoke(d);
    }

    private void HandlePlaybackEnded(PlaybackEndedCommand cmd)
    {
        if (cmd.PlayToken != _playToken) return; // stale
        if (!cmd.IsGameStream) return; // monitor's stop is not authoritative
        DisposeAllPlayers();
        _nowPlaying = null;
        Stopped?.Invoke();
    }

    private void DisposeAllPlayers()
    {
        DisposePlayer(ref _gamePlayer, ref _gameStream, ref _gameMs, ref _gameVol);
        DisposePlayer(ref _monitorPlayer, ref _monitorStream, ref _monitorMs, ref _monitorVol);
    }

    private static void DisposePlayer(ref IWavePlayer? player, ref RawSourceWaveStream? stream,
                                      ref MemoryStream? ms, ref NAudio.Wave.SampleProviders.VolumeSampleProvider? vol)
    {
        if (player is not null) { player.Stop(); player.Dispose(); }
        stream?.Dispose(); ms?.Dispose();
        player = null; stream = null; ms = null; vol = null;
    }
}
