using System.Drawing;
using System.Windows.Forms;
using Soundpad.Network;
using Soundpad.Sound;

namespace Soundpad.Tray;

public class TrayIconHost
{
    private readonly SoundLibrary _library;
    private readonly NetworkAdapter _adapter;
    private readonly int _port;
    private Thread? _uiThread;
    private NotifyIcon? _icon;
    private ApplicationContext? _ctx;

    public TrayIconHost(SoundLibrary library, NetworkAdapter adapter, int port)
    {
        _library = library; _adapter = adapter; _port = port;
    }

    public void Start()
    {
        _uiThread = new Thread(RunUi) { IsBackground = true, Name = "tray-ui" };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    public void Stop()
    {
        try { _ctx?.ExitThread(); } catch { /* best effort */ }
    }

    private void RunUi()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var ip = _adapter.IPv4.First().ToString();
        var url = $"http://{ip}:{_port}/?t={_library.Config.AuthToken}";

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"Soundpad — {ip}:{_port}",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add($"{ip}:{_port}").Click += (_, _) => System.Threading.Tasks.Task.Run(() => Clipboard.SetText(url));
        menu.Items.Add("Mostrar QR code").Click += (_, _) => new QrWindow(url).ShowDialog();
        menu.Items.Add("Abrir pasta de sons").Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Path.Combine(AppContext.BaseDirectory, "sounds"));
        menu.Items.Add("Sair").Click += (_, _) => { Application.Exit(); Environment.Exit(0); };
        _icon.ContextMenuStrip = menu;

        _ctx = new ApplicationContext();
        Application.Run(_ctx);
        _icon.Visible = false;
        _icon.Dispose();
    }
}
