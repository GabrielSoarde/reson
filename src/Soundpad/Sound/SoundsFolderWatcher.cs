// Disambiguate vs. System.Windows.Forms.Timer pulled in by UseWindowsForms=true.
using Timer = System.Threading.Timer;

namespace Soundpad.Sound;

/// <summary>
/// Watches the sounds/ folder for adds, renames, and deletes; debounces
/// burst events (e.g. multi-file paste) and triggers a single library refresh
/// after the burst settles. Lets users drop new mp3s into the folder and see
/// them appear in the UI without restarting the app.
///
/// <para>Lifetime: owned by Program.cs; disposed on ApplicationStopping.</para>
///
/// <para>Threading: <see cref="FileSystemWatcher"/> raises events on
/// ThreadPool threads. We marshal each event through a debounce timer and
/// invoke library mutations from a worker callback — SoundLibrary is
/// internally lock-protected so this is safe.</para>
/// </summary>
public sealed class SoundsFolderWatcher : IDisposable
{
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg", ".flac" };

    private readonly SoundLibrary _library;
    private readonly string _dir;
    private readonly TimeSpan _debounce;
    private readonly Action<Action> _scheduler;
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private readonly object _gate = new();
    // Bitmask of pending event kinds. Keeps minimal state across debounced
    // events so on flush we know whether a deletion-only burst happened
    // (in which case we still need to wake the UI via Changed even though
    // AutoScan won't add anything).
    private bool _sawAdd;
    private bool _sawDelete;
    private bool _disposed;

    public SoundsFolderWatcher(SoundLibrary library, string soundsDir)
        : this(library, soundsDir, TimeSpan.FromMilliseconds(500), action => action()) { }

    /// <summary>Test ctor: customise debounce window and synchronous scheduler.</summary>
    internal SoundsFolderWatcher(SoundLibrary library, string soundsDir, TimeSpan debounce, Action<Action> scheduler)
    {
        _library = library;
        _dir = soundsDir;
        _debounce = debounce;
        _scheduler = scheduler;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SoundsFolderWatcher));
        if (_watcher is not null) return;
        Directory.CreateDirectory(_dir);

        var w = new FileSystemWatcher(_dir)
        {
            // Match the AutoScan whitelist — we'd ignore other extensions
            // on flush anyway, but filtering at the OS level reduces churn.
            // FSW supports a single Filter string; rely on InternalBufferSize +
            // an in-flush whitelist check instead for multi-extension coverage.
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
        };
        w.Created += (_, e) => OnEvent(e.FullPath, isDelete: false);
        w.Deleted += (_, e) => OnEvent(e.FullPath, isDelete: true);
        w.Renamed += (_, e) => { OnEvent(e.OldFullPath, isDelete: true); OnEvent(e.FullPath, isDelete: false); };
        // Changed events fire on metadata updates too — we treat them as adds
        // so the library re-runs AutoScan (a no-op if the file is already
        // known) and re-evaluates runtime statuses.
        w.Changed += (_, e) => OnEvent(e.FullPath, isDelete: false);
        w.EnableRaisingEvents = true;
        _watcher = w;

        _timer = new Timer(_ => Flush(), state: null, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnEvent(string path, bool isDelete)
    {
        // Filter to recognised extensions: ignore .tmp files, OS-managed
        // thumbnails, partial Chrome downloads, etc.
        var ext = Path.GetExtension(path);
        if (!AllowedExtensions.Contains(ext)) return;

        lock (_gate)
        {
            if (_disposed) return;
            if (isDelete) _sawDelete = true; else _sawAdd = true;
            _timer?.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        bool sawAdd, sawDelete;
        lock (_gate)
        {
            if (_disposed) return;
            sawAdd = _sawAdd; sawDelete = _sawDelete;
            _sawAdd = false; _sawDelete = false;
        }
        if (!sawAdd && !sawDelete) return;

        _scheduler(() =>
        {
            try
            {
                // Both adds and deletes only refresh the UI's runtime "missing"
                // status — they never import. Importing arbitrary folder files
                // would re-add sounds the user intentionally removed (the file
                // stays on disk by design). New sounds are added explicitly via
                // the "+" button / upload, which registers a single entry.
                // Re-appearing a previously-missing file clears its missing flag.
                _library.NotifyExternalChange();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"sounds-watcher: flush failed: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _timer?.Dispose(); } catch { }
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
        }
        catch { /* best effort */ }
        _timer = null;
        _watcher = null;
    }
}
