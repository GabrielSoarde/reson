using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Soundpad.Network;
using Soundpad.Sound;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace Soundpad.Wpf;

/// <summary>
/// Connection panel — replaces the old fixed QR sidebar with a popover anchored
/// to the wifi icon in the icon strip. Lists every IPv4 address on every
/// reachable adapter (Ethernet, Wi-Fi, Tailscale, Loopback...) so users on a
/// VPN can pick the address their phone can actually route to.
///
/// <para>The user clicks an IP to make it the QR's target. The recommended LAN
/// address (whatever <see cref="LanAdapterPicker"/> ranks first) starts
/// highlighted, matching the address the tray/console prints on boot.</para>
/// </summary>
internal sealed class ConnectionPopover : Popup
{
    private readonly SoundLibrary _library;
    private readonly NetworkAdapter _defaultAdapter;
    private readonly int _port;
    private Image _qrImage = null!;
    private TextBlock _urlText = null!;
    private StackPanel _ipList = null!;
    private IPAddress _activeIp;

    public ConnectionPopover(SoundLibrary library, NetworkAdapter defaultAdapter, int port)
    {
        _library = library;
        _defaultAdapter = defaultAdapter;
        _port = port;
        _activeIp = defaultAdapter.IPv4.FirstOrDefault() ?? IPAddress.Loopback;

        var card = new Border
        {
            Background = new SolidColorBrush(MainWindow.PanelBg),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(MainWindow.BorderColor),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Width = 540,
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        var title = new TextBlock
        {
            Text = "Conectar celular",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        };
        DockPanel.SetDock(title, Dock.Left);
        var refreshBtn = MainWindow.BuildFlatButton("REFRESH", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 90, 28, 11);
        refreshBtn.Click += (_, _) => Rebuild();
        DockPanel.SetDock(refreshBtn, Dock.Right);
        heading.Children.Add(title);
        heading.Children.Add(refreshBtn);
        Grid.SetRow(heading, 0);
        Grid.SetColumnSpan(heading, 2);
        root.Children.Add(heading);

        var leftScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 280,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _ipList = new StackPanel { Orientation = Orientation.Vertical };
        leftScroll.Content = _ipList;
        Grid.SetColumn(leftScroll, 0);
        Grid.SetRow(leftScroll, 1);
        root.Children.Add(leftScroll);

        var qrFrame = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _qrImage = new Image
        {
            Width = 200,
            Height = 200,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_qrImage, BitmapScalingMode.NearestNeighbor);
        qrFrame.Child = _qrImage;
        Grid.SetColumn(qrFrame, 1);
        Grid.SetRow(qrFrame, 1);
        root.Children.Add(qrFrame);

        _urlText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(_urlText, 2);
        Grid.SetColumnSpan(_urlText, 2);
        root.Children.Add(_urlText);

        card.Child = root;
        Child = card;

        AllowsTransparency = true;

        Rebuild();
    }

    private void Rebuild()
    {
        _ipList.Children.Clear();
        var entries = EnumerateAddresses();
        // Default highlighting: the LanAdapterPicker's first IP on the default
        // adapter (what the tray/console announces). Falls back to the first
        // entry in the table if that adapter is gone.
        if (!entries.Any(e => e.Ip.Equals(_activeIp)) && entries.Count > 0)
            _activeIp = entries[0].Ip;

        foreach (var e in entries)
        {
            _ipList.Children.Add(BuildIpRow(e));
        }
        RefreshQr();
    }

    private Border BuildIpRow(IpEntry entry)
    {
        var isActive = entry.Ip.Equals(_activeIp);
        var row = new Border
        {
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = isActive
                ? new SolidColorBrush(MainWindow.AccentColor)
                : new SolidColorBrush(MainWindow.IconHoverBg),
            Padding = new Thickness(10, 8, 10, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(new TextBlock
        {
            Text = entry.Ip.ToString(),
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = entry.Description,
            Foreground = new SolidColorBrush(Colors.White) { Opacity = 0.7 },
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
        });
        row.Child = stack;
        row.MouseLeftButtonUp += (_, _) =>
        {
            _activeIp = entry.Ip;
            Rebuild();
        };
        return row;
    }

    private void RefreshQr()
    {
        var url = $"http://{_activeIp}:{_port}/?t={_library.Config.AuthToken}";
        try
        {
            _qrImage.Source = QrRenderer.RenderBitmap(url);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ConnectionPopover.RefreshQr: {ex.Message}");
        }
        _urlText.Text = url;
    }

    /// <summary>
    /// Enumerate every IPv4 address on every reachable adapter, sorted with
    /// the recommended LAN address first, then private LAN ranges (Ethernet/
    /// Wireless), then Tailscale/VPN, then loopback last. Description tags
    /// pick out common adapter types so the user can tell them apart.
    /// </summary>
    private List<IpEntry> EnumerateAddresses()
    {
        var entries = new List<IpEntry>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var desc = ClassifyAdapter(ni);
                    entries.Add(new IpEntry(addr.Address, $"{ni.Name} — {desc}"));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ConnectionPopover enumerate: {ex.Message}");
        }
        // Sort: recommended LAN IP (from _defaultAdapter) first, then private
        // ranges, then loopback, then everything else.
        var lanIp = _defaultAdapter.IPv4.FirstOrDefault();
        return entries
            .OrderByDescending(e => lanIp is not null && e.Ip.Equals(lanIp))
            .ThenByDescending(e => IsPrivate(e.Ip))
            .ThenBy(e => IsLoopback(e.Ip))
            .ThenBy(e => e.Ip.ToString())
            .ToList();
    }

    private static string ClassifyAdapter(NetworkInterface ni)
    {
        var hay = (ni.Name + " " + ni.Description).ToLowerInvariant();
        if (hay.Contains("tailscale")) return "Tailscale (VPN)";
        if (hay.Contains("hamachi")) return "Hamachi (VPN)";
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) return "Loopback";
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) return "Ethernet";
        return ni.NetworkInterfaceType.ToString();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    private static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip);

    private record IpEntry(IPAddress Ip, string Description);
}
