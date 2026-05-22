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
            case PlayCommand: break;
            case StopCommand: break;
            case VolumeCommand: break;
            case SetMonitorEnabledCommand: break;
            case SetMonitorDeviceCommand: break;
            case SetGameDeviceCommand: break;
            case PlaybackEndedCommand: break;
            case ShutdownCommand: /* handled by Loop */ break;
        }
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
