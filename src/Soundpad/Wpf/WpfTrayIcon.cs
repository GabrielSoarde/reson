using System.Drawing;
using System.Windows.Forms;
using Soundpad.Network;
using Soundpad.Sound;
using Soundpad.Tray;
using Application = System.Windows.Application;

namespace Soundpad.Wpf;

/// <summary>
/// WPF-thread tray icon. Uses the WinForms <see cref="NotifyIcon"/> because it's
/// already in the project deps (UseWindowsForms=true) and there is no need to
/// pull in Hardcodet.NotifyIcon.Wpf just for the tray.
///
/// Lives on the WPF Dispatcher thread (same STA thread as MainWindow), which
/// satisfies NotifyIcon's STA requirement and lets the menu callbacks touch
/// the MainWindow without marshaling.
/// </summary>
public sealed class WpfTrayIcon : IDisposable
{
    private readonly SoundLibrary _library;
    private readonly NetworkAdapter _adapter;
    private readonly int _port;
    private readonly MainWindow _window;
    private NotifyIcon? _icon;

    public WpfTrayIcon(SoundLibrary library, NetworkAdapter adapter, int port, MainWindow window)
    {
        _library = library;
        _adapter = adapter;
        _port = port;
        _window = window;
        Init();
    }

    private void Init()
    {
        var ip = _adapter.IPv4.First().ToString();
        var url = $"http://{ip}:{_port}/?t={_library.Config.AuthToken}";

        _icon = new NotifyIcon
        {
            Icon = LoadResonIcon() ?? SystemIcons.Application,
            Text = $"Reson — {ip}:{_port}",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar janela").Click += (_, _) => ShowMainWindow();
        menu.Items.Add($"{ip}:{_port}").Click += (_, _) => System.Threading.Tasks.Task.Run(() => Clipboard.SetText(url));
        menu.Items.Add("Mostrar QR code").Click += (_, _) => new QrWindow(url).ShowDialog();
        menu.Items.Add("Abrir pasta de sons").Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Path.Combine(AppContext.BaseDirectory, "sounds"));
        menu.Items.Add("Sair").Click += (_, _) =>
        {
            // Shutdown order: hide tray → stop WPF Application → kill the whole
            // process so the Kestrel app.Run() loop on the main thread also exits.
            try { _icon!.Visible = false; } catch { }
            try { Application.Current?.Shutdown(); } catch { }
            Environment.Exit(0);
        };
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        try
        {
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == System.Windows.WindowState.Minimized)
                _window.WindowState = System.Windows.WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"tray ShowMainWindow: {ex.Message}");
        }
    }

    /// <summary>
    /// Load Reson.ico from the embedded WPF Resource (Soundpad.csproj declares
    /// it under &lt;Resource&gt;). We open the pack URI stream and hand it to
    /// the WinForms <see cref="Icon"/> constructor — this works under
    /// PublishSingleFile=true because pack resources live in the assembly's
    /// resource manifest, not as sibling files. Returns null on any failure;
    /// caller falls back to <see cref="SystemIcons.Application"/>.
    /// </summary>
    private static Icon? LoadResonIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Reson.ico", UriKind.Absolute));
            if (res is null) return null;
            using var s = res.Stream;
            return new Icon(s);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WpfTrayIcon.LoadResonIcon: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_icon is null) return;
        try { _icon.Visible = false; } catch { }
        try { _icon.Dispose(); } catch { }
        _icon = null;
    }
}
