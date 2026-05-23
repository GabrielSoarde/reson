using System.Windows;
using Soundpad.Audio;
using Soundpad.Network;
using Soundpad.Sound;
// System.Windows.Forms.Application is in scope via the WinForms implicit usings,
// so name the WPF one explicitly.
using Application = System.Windows.Application;

namespace Soundpad.Wpf;

/// <summary>
/// Hosts the WPF <see cref="Application"/> on a dedicated STA thread alongside
/// the Kestrel server (which keeps blocking on the main thread). Owns the
/// <see cref="MainWindow"/> and the <see cref="WpfTrayIcon"/>; both live on
/// the same Dispatcher so we can poke them from any thread via Dispatcher.Invoke.
/// </summary>
public sealed class WpfHost
{
    private readonly SoundLibrary _library;
    private readonly PlaybackEngine _engine;
    private readonly DeviceLocator _locator;
    private readonly NetworkAdapter _adapter;
    private readonly int _port;
    private readonly string _soundsDir;
    private Thread? _thread;
    private Application? _app;
    private MainWindow? _window;
    private WpfTrayIcon? _tray;

    public WpfHost(SoundLibrary library, PlaybackEngine engine, DeviceLocator locator, NetworkAdapter adapter, int port, string soundsDir)
    {
        _library = library;
        _engine = engine;
        _locator = locator;
        _adapter = adapter;
        _port = port;
        _soundsDir = soundsDir;
    }

    public void Start()
    {
        if (_thread is not null) return;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => RunUi(ready))
        {
            IsBackground = false,  // keep process alive while WPF runs
            Name = "wpf-ui",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        // Wait briefly for the Dispatcher to come up so callers can post to it.
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Tears down the WPF Application and tray on its own thread. Called from
    /// ApplicationStopping (the Kestrel lifetime callback). Safe to call from
    /// any thread.
    /// </summary>
    public void Stop()
    {
        var app = _app;
        if (app is null) return;
        try
        {
            app.Dispatcher.InvokeAsync(() =>
            {
                try { _tray?.Dispose(); } catch { /* best effort */ }
                try { app.Shutdown(); } catch { /* best effort */ }
            });
        }
        catch { /* Dispatcher may already be gone */ }
    }

    private void RunUi(ManualResetEventSlim ready)
    {
        try
        {
            _app = new Application
            {
                // Without explicit ShutdownMode the app would exit when MainWindow
                // closes — but we hide the window to tray on close, so use Explicit.
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            _window = new MainWindow(_library, _engine, _locator, _adapter, _port, _soundsDir);
            _tray = new WpfTrayIcon(_library, _adapter, _port, _window);
            _window.Show();
            ready.Set();
            _app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wpf-ui crashed: {ex}");
            ready.Set();
        }
    }
}
