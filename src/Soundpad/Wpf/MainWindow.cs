using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Soundpad.Audio;
using Soundpad.Models;
using Soundpad.Network;
using Soundpad.Sound;
// Disambiguate vs. System.Windows.Forms (UseWindowsForms=true is on for the tray)
// and System.Drawing / System.IO which the SDK pulls in globally.
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Soundpad.Wpf;

/// <summary>
/// Native Reson window — the PC twin of the phone web UI.
///
/// Threading: WPF Dispatcher (STA). All engine/library events that arrive on
/// background threads (audio engine loop, Kestrel request threads) must be
/// marshaled via Dispatcher.BeginInvoke before touching UI state.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly SoundLibrary _library;
    private readonly PlaybackEngine _engine;
    private readonly DeviceLocator _locator;
    private readonly NetworkAdapter _adapter;
    private readonly int _port;
    private readonly string _soundsDir;
    private readonly Action<string> _onPlaying;
    private readonly Action _onStopped;
    private readonly Action<int> _onVolumeChanged;
    private readonly Action<bool> _onMonitorChanged;
    private readonly Action _onLibraryChanged;

    private static readonly Color BgColor = (Color)ColorConverter.ConvertFromString("#1f2937");
    private static readonly Color PanelColor = (Color)ColorConverter.ConvertFromString("#111827");
    private static readonly Color StopColor = (Color)ColorConverter.ConvertFromString("#dc2626");
    private static readonly Color StopHoverColor = (Color)ColorConverter.ConvertFromString("#ef4444");
    private static readonly Color MutedTextColor = (Color)ColorConverter.ConvertFromString("#cbd5e1");
    private static readonly Color AccentColor = (Color)ColorConverter.ConvertFromString("#22c55e");
    private static readonly Color AccentHoverColor = (Color)ColorConverter.ConvertFromString("#16a34a");
    private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#374151");
    private static readonly Color NeutralHoverColor = (Color)ColorConverter.ConvertFromString("#4b5563");

    // QR sidebar widths. Expanded ~= QR + padding; collapsed = a thin re-open strip.
    private const double SidebarExpandedWidth = 280;
    private const double SidebarCollapsedWidth = 36;

    private Grid _gridHost = null!;
    private Slider _volumeSlider = null!;
    private TextBlock _volumeLabel = null!;
    private CheckBox _monitorCheck = null!;
    private string? _currentlyPlayingId;
    // Map sound id → its visual root (Border) so we can flip the "playing" style fast.
    private readonly Dictionary<string, SoundTile> _tilesById = new();
    private bool _suppressEngineEcho;  // ignore the next VolumeChanged when we caused it
    // Track the token we last rendered so we don't re-render the QR on every
    // libraryChanged (most of which are sound add/remove, not token rotation).
    private string? _lastRenderedToken;

    // Sidebar pieces — kept as fields because they need updating from event handlers
    // (token-regenerate refreshes the QR; collapse swaps content visibility).
    private ColumnDefinition _sidebarCol = null!;
    private Border _sidebar = null!;
    private StackPanel _sidebarContent = null!;
    private StackPanel _sidebarCollapsed = null!;
    private Image _qrImage = null!;
    private TextBlock _urlText = null!;
    private bool _sidebarCollapsedState;

    public MainWindow(SoundLibrary library, PlaybackEngine engine, DeviceLocator locator, NetworkAdapter adapter, int port, string soundsDir)
    {
        _library = library;
        _engine = engine;
        _locator = locator;
        _adapter = adapter;
        _port = port;
        _soundsDir = soundsDir;

        Title = "Reson";
        Width = 1180;
        Height = 680;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(BgColor);
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        TrySetWindowIcon();

        BuildLayout();
        RebuildGrid();
        RefreshSidebar();

        _onPlaying = id => Dispatcher.BeginInvoke(new Action(() => OnPlayingUi(id)));
        _onStopped = () => Dispatcher.BeginInvoke(new Action(OnStoppedUi));
        _onVolumeChanged = v => Dispatcher.BeginInvoke(new Action(() => OnVolumeChangedUi(v)));
        _onMonitorChanged = b => Dispatcher.BeginInvoke(new Action(() => OnMonitorChangedUi(b)));
        _onLibraryChanged = () => Dispatcher.BeginInvoke(new Action(() =>
        {
            RebuildGrid();
            // Library mutations include token rotation (POST /api/auth/regenerate
            // persists via Save() which raises Changed). RefreshSidebar diffs
            // against _lastRenderedToken so it's cheap on the common case.
            RefreshSidebar();
        }));

        _engine.Playing += _onPlaying;
        _engine.Stopped += _onStopped;
        _engine.VolumeChanged += _onVolumeChanged;
        _engine.MonitorChanged += _onMonitorChanged;
        _library.Changed += _onLibraryChanged;

        // Lifecycle hooks live here (not inside the first-run warning) so they
        // attach regardless of whether the user has a device configured. A
        // prior bug wired these inside WarnIfNoAudioDevice, which meant once
        // the user picked a device the X button stopped hide-to-tray.
        Closing += OnClosingHideToTray;
        Closed += OnClosedUnsubscribe;

        Loaded += (_, _) => WarnIfNoAudioDevice();
    }

    /// <summary>
    /// Load Reson.ico from the embedded WPF resource manifest and apply it as
    /// the window icon (title bar + alt-tab + taskbar). The .ico is shipped as
    /// a <Resource> in the csproj, which works under PublishSingleFile=true
    /// because the resource manifest is part of the assembly itself. Failures
    /// are swallowed — without an icon WPF falls back to the default app glyph,
    /// which is annoying but not fatal.
    /// </summary>
    private void TrySetWindowIcon()
    {
        try
        {
            // Resource pack URI: app:,,, scheme + the Link path from the
            // <Resource> ItemGroup in Soundpad.csproj.
            var uri = new Uri("pack://application:,,,/Reson.ico", UriKind.Absolute);
            Icon = BitmapFrame.Create(uri);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MainWindow.TrySetWindowIcon: {ex.Message}");
        }
    }

    // Note: when MicDevice in the config is null, the engine uses the system
    // default capture device. The first-run warning below only complains about
    // a missing virtual cable for the OUTPUT side; the mic side falls back
    // gracefully via WasapiMicCapture.Start(null, ...). Per-device mic
    // selection UI is deferred to a future iteration; until then users can
    // POST /api/mic/device to override.
    private void WarnIfNoAudioDevice()
    {
        if (!string.IsNullOrEmpty(_library.Config.AudioDevice)) return;

        // Branch on whether the system has *any* render devices. If yes, the
        // user just hasn't picked one yet — offer the Settings dialog. If no,
        // there's nothing to pick from and the right action is to install a
        // virtual cable, so we fall through to the VB-Cable prompt.
        IReadOnlyList<AudioDeviceInfo> outs;
        try { outs = _locator.EnumerateRenderDevices(); } catch { outs = Array.Empty<AudioDeviceInfo>(); }

        if (outs.Count > 0)
        {
            var pickNow = System.Windows.MessageBox.Show(
                "Nenhum dispositivo de saída está configurado, mas há dispositivos disponíveis no sistema.\n\n" +
                "Deseja abrir as configurações agora para escolher um?",
                "Reson — escolha um dispositivo",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (pickNow == System.Windows.MessageBoxResult.Yes)
            {
                OpenSettingsDialog();
            }
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "Nenhum cabo de áudio virtual foi detectado.\n\n" +
            "O Reson precisa de VB-Cable ou VoiceMeeter instalado para enviar som ao Discord/Valorant.\n\n" +
            "Deseja abrir a página de download do VB-Cable agora?",
            "Reson — dispositivo de áudio ausente",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://vb-audio.com/Cable/",
                    UseShellExecute = true,
                });
            }
            catch { /* user can navigate manually */ }
        }
    }

    private void BuildLayout()
    {
        // Top-level: 2 columns (main pane | QR sidebar). The sidebar's width
        // is animatable via _sidebarCol so we don't reflow the rest of the UI
        // when the user collapses/expands.
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sidebarCol = new ColumnDefinition { Width = new GridLength(SidebarExpandedWidth) };
        root.ColumnDefinitions.Add(_sidebarCol);

        // Left column: header / grid / bottom bar (the original 3-row layout).
        var leftPane = new Grid();
        leftPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        leftPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // grid
        leftPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // bottom bar

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        leftPane.Children.Add(header);

        // Grid host (sound buttons live here)
        _gridHost = new Grid
        {
            Margin = new Thickness(20, 20, 20, 12),
        };
        Grid.SetRow(_gridHost, 1);
        leftPane.Children.Add(_gridHost);

        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 2);
        leftPane.Children.Add(bottom);

        Grid.SetColumn(leftPane, 0);
        root.Children.Add(leftPane);

        // Right column: QR sidebar (built lazily into a Border so we can swap
        // expanded ↔ collapsed content without reflowing the main grid).
        _sidebar = BuildSidebar();
        Grid.SetColumn(_sidebar, 1);
        root.Children.Add(_sidebar);

        Content = root;
    }

    private Border BuildHeader()
    {
        var header = new Border
        {
            Background = new SolidColorBrush(PanelColor),
            Padding = new Thickness(20, 14, 20, 14),
        };
        var headerStack = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new Ellipse
        {
            Width = 12, Height = 12,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22c55e")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var titleText = new TextBlock
        {
            Text = "Reson",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerStack.Children.Add(dot);
        headerStack.Children.Add(titleText);
        header.Child = headerStack;
        return header;
    }

    /// <summary>
    /// Build the right-side QR sidebar. The sidebar has two visual states —
    /// expanded (QR + URL + actions) and collapsed (a thin strip with an
    /// "expand" arrow). Both states share the same Border host; we just swap
    /// the Child Grid and animate the column width.
    /// </summary>
    private Border BuildSidebar()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(PanelColor),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0b1220")),
        };

        // Expanded content
        _sidebarContent = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(16, 16, 16, 16),
        };

        var headerRow = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };
        var headerTitle = new TextBlock
        {
            Text = "Conectar celular",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(headerTitle, Dock.Left);
        var collapseBtn = BuildFlatButton("Esconder", NeutralColor, NeutralHoverColor, width: 88, height: 28, fontSize: 11);
        collapseBtn.Click += (_, _) => CollapseSidebar();
        DockPanel.SetDock(collapseBtn, Dock.Right);
        headerRow.Children.Add(headerTitle);
        headerRow.Children.Add(collapseBtn);
        _sidebarContent.Children.Add(headerRow);

        // QR image — DecodePixelWidth in QrRenderer keeps memory bounded.
        var qrFrame = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _qrImage = new Image
        {
            Width = 220,
            Height = 220,
            Stretch = Stretch.Uniform,
            // Pixelated source: nearest-neighbor keeps the QR modules crisp
            // at any DPI (default WPF bilinear blurs the black squares).
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_qrImage, BitmapScalingMode.NearestNeighbor);
        qrFrame.Child = _qrImage;
        _sidebarContent.Children.Add(qrFrame);

        // URL text under the QR (selectable feels nice but a plain TextBlock
        // wraps fine and the "Copiar URL" button covers the copy use case).
        _urlText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(MutedTextColor),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _sidebarContent.Children.Add(_urlText);

        // Action buttons stacked below the URL.
        var copyBtn = BuildFlatButton("Copiar URL", AccentColor, AccentHoverColor, width: double.NaN, height: 32, fontSize: 12);
        copyBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        copyBtn.Margin = new Thickness(0, 12, 0, 0);
        copyBtn.Click += (_, _) => CopyUrlToClipboard();
        _sidebarContent.Children.Add(copyBtn);

        var regenBtn = BuildFlatButton("Regenerar token", NeutralColor, NeutralHoverColor, width: double.NaN, height: 32, fontSize: 12);
        regenBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        regenBtn.Margin = new Thickness(0, 8, 0, 0);
        regenBtn.Click += (_, _) => RegenerateToken();
        _sidebarContent.Children.Add(regenBtn);

        var hint = new TextBlock
        {
            Text = "Escaneie o QR com o celular na mesma rede Wi-Fi.\n\nRegenerar o token desconecta todos os celulares — eles precisam escanear de novo.",
            FontSize = 10,
            Foreground = new SolidColorBrush(MutedTextColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            Opacity = 0.75,
        };
        _sidebarContent.Children.Add(hint);

        // Collapsed state: just a vertical text "→" expand button. The grip
        // takes the full sidebar width so the user has a wide click target.
        _sidebarCollapsed = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var expandBtn = new Button
        {
            Content = "‹\nQ\nR",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Width = 28,
            Height = 80,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(NeutralColor),
            ToolTip = "Mostrar QR",
        };
        expandBtn.Template = BuildFlatButtonTemplate(NeutralColor, NeutralHoverColor, cornerRadius: 6);
        expandBtn.Click += (_, _) => ExpandSidebar();
        _sidebarCollapsed.Children.Add(expandBtn);
        _sidebarCollapsed.Visibility = Visibility.Collapsed;

        var hostStack = new Grid();
        hostStack.Children.Add(_sidebarContent);
        hostStack.Children.Add(_sidebarCollapsed);
        border.Child = hostStack;

        return border;
    }

    /// <summary>
    /// Toggle the sidebar to the collapsed state — narrow strip with an
    /// expand glyph. We don't animate the column width because GridLength
    /// is non-animatable; the snap is fine for this UI and keeps the code
    /// simple.
    /// </summary>
    private void CollapseSidebar()
    {
        _sidebarCollapsedState = true;
        _sidebarContent.Visibility = Visibility.Collapsed;
        _sidebarCollapsed.Visibility = Visibility.Visible;
        _sidebarCol.Width = new GridLength(SidebarCollapsedWidth);
    }

    private void ExpandSidebar()
    {
        _sidebarCollapsedState = false;
        _sidebarContent.Visibility = Visibility.Visible;
        _sidebarCollapsed.Visibility = Visibility.Collapsed;
        _sidebarCol.Width = new GridLength(SidebarExpandedWidth);
        // Re-render in case token rotated while collapsed.
        RefreshSidebar();
    }

    /// <summary>
    /// Build the URL the phone uses to connect. Mirrors the format used by
    /// <see cref="WpfTrayIcon"/> and the bootstrap log line in Program.cs.
    /// </summary>
    private string BuildConnectUrl()
    {
        var ip = _adapter.IPv4.FirstOrDefault()?.ToString() ?? "127.0.0.1";
        return $"http://{ip}:{_port}/?t={_library.Config.AuthToken}";
    }

    /// <summary>
    /// Refresh the QR + URL text. Cheap on the no-token-change path: short-
    /// circuits via <see cref="_lastRenderedToken"/> so library mutations
    /// (sound add/remove) don't redo the bitmap encode.
    /// </summary>
    private void RefreshSidebar()
    {
        if (_sidebarCollapsedState) return; // nothing visible to refresh
        var token = _library.Config.AuthToken;
        if (token == _lastRenderedToken && _qrImage.Source is not null) return;
        var url = BuildConnectUrl();
        try
        {
            _qrImage.Source = QrRenderer.RenderBitmap(url);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MainWindow.RefreshSidebar render: {ex.Message}");
        }
        _urlText.Text = url;
        _lastRenderedToken = token;
    }

    private void CopyUrlToClipboard()
    {
        var url = BuildConnectUrl();
        try
        {
            System.Windows.Clipboard.SetText(url);
        }
        catch (Exception ex)
        {
            // Clipboard can throw COM ExternalException on transient locks.
            // Surface to the user so they know to retry rather than silently
            // pretending it worked.
            System.Windows.MessageBox.Show(
                $"Não foi possível copiar a URL:\n{ex.Message}",
                "Reson",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Rotate the auth token directly via the library (no HTTP round-trip —
    /// we're in the same process). Identical persistence path to the
    /// /api/auth/regenerate endpoint, so the behavior matches what a phone
    /// hitting that endpoint would see. After Save() the library raises
    /// Changed → _onLibraryChanged → RefreshSidebar re-renders the QR with
    /// the new token.
    /// </summary>
    private void RegenerateToken()
    {
        var confirm = System.Windows.MessageBox.Show(
            "Isto vai desconectar todos os celulares que já fizeram pareamento. " +
            "Eles vão precisar escanear o QR novamente.\n\nDeseja continuar?",
            "Reson — regenerar token",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            var newToken = GenerateToken();
            _library.MutateConfig(c => c with { AuthToken = newToken });
            _library.Save(); // raises Changed → RefreshSidebar via _onLibraryChanged
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Falha ao regenerar token:\n{ex.Message}",
                "Reson",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private static string GenerateToken()
    {
        // Match SoundConfig.GenerateToken and AuthEndpoints.GenerateToken —
        // 16-byte hex, lowercased. Kept duplicated here (rather than centralized)
        // so the WPF assembly doesn't need to reference an HTTP-flavored helper.
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private FrameworkElement BuildBottomBar()
    {
        var bar = new Border
        {
            Background = new SolidColorBrush(PanelColor),
            Padding = new Thickness(20, 16, 20, 18),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // STOP
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Volume
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // Monitor
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // Add sound
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // Settings

        // STOP button (left)
        var stopButton = BuildStopButton();
        Grid.SetColumn(stopButton, 0);
        grid.Children.Add(stopButton);

        // Volume slider (center)
        var volStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 24, 0),
        };
        var volHeader = new DockPanel { LastChildFill = false };
        var volTitle = new TextBlock
        {
            Text = "Volume",
            Foreground = new SolidColorBrush(MutedTextColor),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        DockPanel.SetDock(volTitle, Dock.Left);
        _volumeLabel = new TextBlock
        {
            Text = $"{_library.Config.Volume}%",
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        DockPanel.SetDock(_volumeLabel, Dock.Right);
        volHeader.Children.Add(volTitle);
        volHeader.Children.Add(_volumeLabel);

        _volumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = _library.Config.Volume,
            SmallChange = 1,
            LargeChange = 10,
            TickFrequency = 10,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 4, 0, 0),
            Focusable = true,
        };
        _volumeSlider.ValueChanged += OnVolumeSliderChanged;

        volStack.Children.Add(volHeader);
        volStack.Children.Add(_volumeSlider);
        Grid.SetColumn(volStack, 1);
        grid.Children.Add(volStack);

        // Monitor checkbox (right)
        _monitorCheck = new CheckBox
        {
            Content = "Monitor",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = _library.Config.MonitorEnabled,
        };
        _monitorCheck.Checked += OnMonitorCheckChanged;
        _monitorCheck.Unchecked += OnMonitorCheckChanged;
        Grid.SetColumn(_monitorCheck, 2);
        grid.Children.Add(_monitorCheck);

        // "+ Adicionar som" button — opens an OpenFileDialog and copies the
        // picked files into _soundsDir. FileSystemWatcher → AutoScan picks
        // them up and raises library.Changed which rebuilds the grid.
        var addBtn = BuildAddSoundButton();
        Grid.SetColumn(addBtn, 3);
        grid.Children.Add(addBtn);

        // Settings button (gear) — opens the device picker modal.
        var settingsBtn = BuildSettingsButton();
        Grid.SetColumn(settingsBtn, 4);
        grid.Children.Add(settingsBtn);

        bar.Child = grid;
        return bar;
    }

    private Button BuildSettingsButton()
    {
        var btn = BuildFlatButton("⚙ Configurações", NeutralColor, NeutralHoverColor, width: 150, height: 36, fontSize: 13);
        btn.Margin = new Thickness(8, 0, 0, 0);
        btn.Click += (_, _) => OpenSettingsDialog();
        return btn;
    }

    private Button BuildAddSoundButton()
    {
        var btn = BuildFlatButton("+ Adicionar som", AccentColor, AccentHoverColor, width: 150, height: 36, fontSize: 13);
        btn.Margin = new Thickness(16, 0, 0, 0);
        btn.Click += (_, _) => AddSoundsFromDialog();
        return btn;
    }

    /// <summary>
    /// Open a multi-select OpenFileDialog and copy each picked file directly
    /// into the user's sounds directory. We deliberately do NOT go through
    /// the /api/sounds/upload HTTP endpoint — we're in the same process, and
    /// File.Copy + FileSystemWatcher → AutoScan handles dedupe (existing file
    /// names skip via OverwriteExisting=false) and grid placement on the same
    /// code path as a manual drop into the folder.
    ///
    /// Failures are aggregated and surfaced as a single MessageBox so a
    /// partial-success batch doesn't spam dialogs.
    /// </summary>
    private void AddSoundsFromDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar sons",
            Multiselect = true,
            Filter = "Audio (*.mp3;*.wav;*.ogg;*.flac)|*.mp3;*.wav;*.ogg;*.flac|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (dlg.FileNames.Length == 0) return;

        try { Directory.CreateDirectory(_soundsDir); } catch { /* AutoScan will skip if missing */ }

        var failed = new List<string>();
        var copied = 0;
        foreach (var src in dlg.FileNames)
        {
            try
            {
                var name = Path.GetFileName(src);
                var dst = Path.Combine(_soundsDir, name);
                // Don't clobber an existing file silently — generate a
                // " (N)" variant the same way SoundLibrary.CreateExclusive
                // does for HTTP uploads. Caps the suffix to avoid infinite
                // loops if someone has hundreds of name collisions.
                if (File.Exists(dst))
                {
                    var stem = Path.GetFileNameWithoutExtension(name);
                    var ext = Path.GetExtension(name);
                    var picked = false;
                    for (int i = 2; i <= 100; i++)
                    {
                        var candidate = Path.Combine(_soundsDir, $"{stem} ({i}){ext}");
                        if (!File.Exists(candidate)) { dst = candidate; picked = true; break; }
                    }
                    if (!picked) { failed.Add($"{name} (muitas colisões de nome)"); continue; }
                }
                File.Copy(src, dst, overwrite: false);
                copied++;
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(src)}: {ex.Message}");
            }
        }

        // Belt-and-suspenders: the FileSystemWatcher in Program.cs runs
        // AutoScan on its own, but its debounce can take a beat. Kick a
        // manual AutoScan so the new sounds show up the moment the dialog
        // closes. Idempotent — AutoScan checks `known` before adding.
        try { _library.AutoScan(); } catch (Exception ex) { Console.Error.WriteLine($"AutoScan after add: {ex.Message}"); }

        if (failed.Count > 0)
        {
            System.Windows.MessageBox.Show(
                $"Copiados {copied} de {dlg.FileNames.Length} arquivos. Falhas:\n\n• " + string.Join("\n• ", failed),
                "Reson — adicionar sons",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void OpenSettingsDialog()
    {
        try
        {
            var dlg = new SettingsWindow(_library, _locator, _port) { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Não foi possível abrir as configurações:\n{ex.Message}",
                "Reson",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private Button BuildStopButton()
    {
        var btn = new Button
        {
            Content = "STOP",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Width = 110,
            Height = 44,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(StopColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Template = BuildFlatButtonTemplate(StopColor, StopHoverColor);
        btn.Click += (_, _) => _engine.Stop();
        return btn;
    }

    /// <summary>
    /// Shared button factory for the various flat-styled buttons on this
    /// window. Pass <c>double.NaN</c> for <paramref name="width"/> to let
    /// the button size to its container (e.g. sidebar full-width buttons).
    /// </summary>
    private static Button BuildFlatButton(string label, Color rest, Color hover, double width, double height, double fontSize)
    {
        var btn = new Button
        {
            Content = label,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = fontSize,
            Height = height,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(rest),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!double.IsNaN(width)) btn.Width = width;
        btn.Template = BuildFlatButtonTemplate(rest, hover);
        return btn;
    }

    private static ControlTemplate BuildFlatButtonTemplate(Color rest, Color hover, double cornerRadius = 10)
    {
        // We can't pass colors into a ControlTemplate easily without static
        // resources, so build the template per-instance with literal brushes
        // via a FrameworkElementFactory.
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bg";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(rest));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hover), "Bg"));
        template.Triggers.Add(hoverTrigger);
        return template;
    }

    /// <summary>
    /// Rebuilds the sound button grid from scratch. Called on init and whenever
    /// SoundLibrary.Changed fires (add/remove/rename from the phone).
    /// </summary>
    private void RebuildGrid()
    {
        _gridHost.Children.Clear();
        _gridHost.ColumnDefinitions.Clear();
        _gridHost.RowDefinitions.Clear();
        _tilesById.Clear();

        var config = _library.Config;
        var cols = Math.Max(1, config.Grid.Cols);
        var rows = Math.Max(1, config.Grid.Rows);

        for (int c = 0; c < cols; c++)
            _gridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++)
            _gridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Sounds live in the same %LOCALAPPDATA%\Reson\sounds\ folder the
        // upload pipeline writes to — passed down from Program.cs via WpfHost.
        foreach (var sound in config.Sounds.Where(s => s.Position is not null))
        {
            // Skip cells that fell off after a grid shrink.
            if (sound.Position!.Col >= cols || sound.Position.Row >= rows) continue;
            var tile = BuildSoundTile(sound, _soundsDir);
            Grid.SetColumn(tile.Root, sound.Position.Col);
            Grid.SetRow(tile.Root, sound.Position.Row);
            _gridHost.Children.Add(tile.Root);
            _tilesById[sound.Id] = tile;
        }

        // Restore the playing-border if engine state survived a rebuild.
        if (_currentlyPlayingId is not null && _tilesById.TryGetValue(_currentlyPlayingId, out var current))
            ApplyPlayingStyle(current, true);
    }

    private SoundTile BuildSoundTile(SoundEntry sound, string soundsDir)
    {
        var path = Path.Combine(soundsDir, sound.File);
        var missing = !File.Exists(path);
        var fill = ParseColorOrFallback(sound.Color);

        var border = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(fill),
            BorderThickness = new Thickness(0),
            BorderBrush = Brushes.White,
            Cursor = missing ? Cursors.No : Cursors.Hand,
            SnapsToDevicePixels = true,
        };

        // Soft drop shadow for depth (cheap; WPF caches it).
        border.Effect = new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.35,
            Color = Colors.Black,
        };

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(sound.Label) ? sound.Id : sound.Label,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10),
        };

        if (missing)
        {
            border.Opacity = 0.4;
            label.Text += "\n(missing)";
        }

        border.Child = label;

        if (!missing)
        {
            // Pointer events: capture click on the Border (it isn't a Button so
            // we use MouseLeftButtonUp to keep the visual minimal). Hover effect
            // is a brightness tweak via Opacity to avoid color math.
            border.MouseLeftButtonUp += (_, _) =>
            {
                try { _engine.Play(sound.Id, path); }
                catch (Exception ex) { Console.Error.WriteLine($"play {sound.Id}: {ex.Message}"); }
            };
            border.MouseEnter += (_, _) => border.Opacity = 0.92;
            border.MouseLeave += (_, _) => border.Opacity = 1.0;
        }

        return new SoundTile(border, label);
    }

    private static Color ParseColorOrFallback(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return (Color)ColorConverter.ConvertFromString("#3b82f6"); }
    }

    private void ApplyPlayingStyle(SoundTile tile, bool playing)
    {
        if (playing)
        {
            tile.Root.BorderThickness = new Thickness(3);
            // Subtle pulse via animation on the white glow effect.
            var glow = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 0,
                Color = Colors.White,
                Opacity = 0.6,
            };
            tile.Root.Effect = glow;
            var pulse = new DoubleAnimation
            {
                From = 0.35,
                To = 0.85,
                Duration = TimeSpan.FromMilliseconds(800),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
        }
        else
        {
            tile.Root.BorderThickness = new Thickness(0);
            tile.Root.Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.35,
                Color = Colors.Black,
            };
        }
    }

    // ---------- engine event handlers (already marshaled to Dispatcher) ----------

    private void OnPlayingUi(string id)
    {
        // Clear the previous tile, set the new one.
        if (_currentlyPlayingId is not null && _tilesById.TryGetValue(_currentlyPlayingId, out var prev))
            ApplyPlayingStyle(prev, false);
        _currentlyPlayingId = id;
        if (_tilesById.TryGetValue(id, out var tile))
            ApplyPlayingStyle(tile, true);
    }

    private void OnStoppedUi()
    {
        if (_currentlyPlayingId is not null && _tilesById.TryGetValue(_currentlyPlayingId, out var prev))
            ApplyPlayingStyle(prev, false);
        _currentlyPlayingId = null;
    }

    private void OnVolumeChangedUi(int v)
    {
        // The engine raised this — could be us or could be the phone. We don't
        // know, so accept the value and avoid re-broadcasting via slider.
        _suppressEngineEcho = true;
        try
        {
            _volumeSlider.Value = v;
            _volumeLabel.Text = $"{v}%";
        }
        finally
        {
            _suppressEngineEcho = false;
        }
    }

    private void OnMonitorChangedUi(bool b)
    {
        _suppressEngineEcho = true;
        try { _monitorCheck.IsChecked = b; }
        finally { _suppressEngineEcho = false; }
    }

    // ---------- UI input handlers ----------

    private void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEngineEcho) return;
        var v = (int)Math.Round(e.NewValue);
        _volumeLabel.Text = $"{v}%";
        // Push to engine + persist.
        _engine.SetVolume(v);
        try
        {
            _library.MutateConfig(c => c with { Volume = v });
            _library.Save();
        }
        catch (Exception ex) { Console.Error.WriteLine($"persist volume: {ex.Message}"); }
    }

    private void OnMonitorCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEngineEcho) return;
        var b = _monitorCheck.IsChecked == true;
        _engine.SetMonitorEnabled(b);
        try
        {
            _library.MutateConfig(c => c with { MonitorEnabled = b });
            _library.Save();
        }
        catch (Exception ex) { Console.Error.WriteLine($"persist monitor: {ex.Message}"); }
    }

    // ---------- lifecycle: hide-to-tray + unsubscribe ----------

    private void OnClosingHideToTray(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Cancel close → Hide. The tray's "Sair" entry exits via Environment.Exit,
        // which bypasses Closing entirely. WpfHost.Stop() calls Application.Shutdown()
        // which terminates the Dispatcher without raising window Closing events.
        // So if we got here at all, it was the user clicking the X.
        e.Cancel = true;
        Hide();
    }

    private void OnClosedUnsubscribe(object? sender, EventArgs e)
    {
        // Reached only on a real Close (not the cancel-and-hide path). Best-effort
        // detach so we don't leak handlers if someone ever does close us properly.
        try
        {
            _engine.Playing -= _onPlaying;
            _engine.Stopped -= _onStopped;
            _engine.VolumeChanged -= _onVolumeChanged;
            _engine.MonitorChanged -= _onMonitorChanged;
            _library.Changed -= _onLibraryChanged;
        }
        catch { /* best effort */ }
    }

    private readonly record struct SoundTile(Border Root, TextBlock Label);
}
