using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// Native Reson window — Deckboard-inspired layout with an icon sidebar,
/// boards panel, and the active board's sound grid.
///
/// <para>Column structure (left to right):</para>
/// <list type="number">
///   <item>~56 px icon strip (Boards / Connection / Settings)</item>
///   <item>~200 px boards panel (board pills + "+" to add)</item>
///   <item>The rest — board header + sound grid + bottom bar with master volume</item>
/// </list>
///
/// <para>Threading: WPF Dispatcher (STA). All engine/library events that arrive on
/// background threads (audio engine loop, Kestrel request threads) must be
/// marshaled via Dispatcher.BeginInvoke before touching UI state.</para>
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

    // Reson palette (Deckboard-inspired, darker variant).
    internal static readonly Color WindowBg = (Color)ColorConverter.ConvertFromString("#1f2937");
    internal static readonly Color IconBarBg = (Color)ColorConverter.ConvertFromString("#0f172a");
    internal static readonly Color BoardsPanelBg = (Color)ColorConverter.ConvertFromString("#1e293b");
    internal static readonly Color PanelBg = (Color)ColorConverter.ConvertFromString("#111827");
    internal static readonly Color BorderColor = (Color)ColorConverter.ConvertFromString("#334155");
    internal static readonly Color AccentColor = (Color)ColorConverter.ConvertFromString("#3b82f6");
    internal static readonly Color AccentHoverColor = (Color)ColorConverter.ConvertFromString("#2563eb");
    internal static readonly Color StopColor = (Color)ColorConverter.ConvertFromString("#dc2626");
    internal static readonly Color StopHoverColor = (Color)ColorConverter.ConvertFromString("#ef4444");
    internal static readonly Color MutedTextColor = (Color)ColorConverter.ConvertFromString("#cbd5e1");
    internal static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#374151");
    internal static readonly Color NeutralHoverColor = (Color)ColorConverter.ConvertFromString("#4b5563");
    internal static readonly Color IconHoverBg = (Color)ColorConverter.ConvertFromString("#1e293b");

    private const double IconBarWidth = 56;
    private const double BoardsPanelWidth = 220;

    private Grid _gridHost = null!;
    private Slider _volumeSlider = null!;
    private TextBlock _volumeLabel = null!;
    private CheckBox _monitorCheck = null!;
    private StackPanel _boardsList = null!;
    private TextBlock _boardHeaderName = null!;
    private string? _currentlyPlayingId;
    private readonly Dictionary<string, Border> _tilesById = new();
    private bool _suppressEngineEcho;
    private ConnectionPopover? _connectionPopover;
    private Button _connectionIconBtn = null!;

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
        Height = 720;
        MinWidth = 820;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(WindowBg);
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        TrySetWindowIcon();

        BuildLayout();
        RebuildBoardsList();
        RebuildGrid();

        _onPlaying = id => Dispatcher.BeginInvoke(new Action(() => OnPlayingUi(id)));
        _onStopped = () => Dispatcher.BeginInvoke(new Action(OnStoppedUi));
        _onVolumeChanged = v => Dispatcher.BeginInvoke(new Action(() => OnVolumeChangedUi(v)));
        _onMonitorChanged = b => Dispatcher.BeginInvoke(new Action(() => OnMonitorChangedUi(b)));
        _onLibraryChanged = () => Dispatcher.BeginInvoke(new Action(() =>
        {
            RebuildBoardsList();
            RebuildGrid();
        }));

        _engine.Playing += _onPlaying;
        _engine.Stopped += _onStopped;
        _engine.VolumeChanged += _onVolumeChanged;
        _engine.MonitorChanged += _onMonitorChanged;
        _library.Changed += _onLibraryChanged;

        Closing += OnClosingHideToTray;
        Closed += OnClosedUnsubscribe;

        Loaded += (_, _) =>
        {
            WarnIfNoAudioDevice();
            // Fire-and-forget update check — never blocks startup, never throws.
            // CheckAndPromptAsync swallows network errors and only prompts when a
            // strictly-newer release with a ResonSetup.exe asset exists.
            _ = UpdatePrompter.CheckAndPromptAsync(this, silentWhenUpToDate: true);
        };
    }

    /// <summary>
    /// Manual "Verificar atualizações" entry point (tray menu). Reports the
    /// result even when up to date / offline, unlike the silent startup check.
    /// </summary>
    public void CheckForUpdatesManually()
        => _ = UpdatePrompter.CheckAndPromptAsync(this, silentWhenUpToDate: false);

    /// <summary>
    /// Load Reson.ico from the embedded WPF resource manifest and apply it as
    /// the window icon (title bar + alt-tab + taskbar).
    /// </summary>
    private void TrySetWindowIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Reson.ico", UriKind.Absolute);
            Icon = BitmapFrame.Create(uri);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MainWindow.TrySetWindowIcon: {ex.Message}");
        }
    }

    private void WarnIfNoAudioDevice()
    {
        if (!string.IsNullOrEmpty(_library.Config.AudioDevice)) return;

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
        // Three-column root grid: icon strip | boards panel | main pane.
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconBarWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BoardsPanelWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBar = BuildIconBar();
        Grid.SetColumn(iconBar, 0);
        root.Children.Add(iconBar);

        var boardsPanel = BuildBoardsPanel();
        Grid.SetColumn(boardsPanel, 1);
        root.Children.Add(boardsPanel);

        var mainPane = BuildMainPane();
        Grid.SetColumn(mainPane, 2);
        root.Children.Add(mainPane);

        Content = root;
    }

    // ─── icon sidebar ────────────────────────────────────────────────────

    private Border BuildIconBar()
    {
        var host = new Border
        {
            Background = new SolidColorBrush(IconBarBg),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(BorderColor),
        };
        var stack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 12, 0, 12) };

        // The "Boards" icon is always active (Boards is the only main view
        // for now — Connection is a popover and Settings is a modal). We
        // still render it with the active styling so the icon strip doesn't
        // look unstuck visually.
        var boardsBtn = BuildIconButton("▤", "Boards", active: true);
        boardsBtn.Click += (_, _) => { /* already on boards view */ };
        stack.Children.Add(boardsBtn);

        _connectionIconBtn = BuildIconButton("📶", "Conexão", active: false);
        _connectionIconBtn.Click += (_, _) => ToggleConnectionPopover();
        stack.Children.Add(_connectionIconBtn);

        var settingsBtn = BuildIconButton("⚙", "Configurações", active: false);
        settingsBtn.Click += (_, _) => OpenSettingsDialog();
        stack.Children.Add(settingsBtn);

        host.Child = stack;
        return host;
    }

    private static Button BuildIconButton(string glyph, string tooltip, bool active)
    {
        var btn = new Button
        {
            Content = glyph,
            Foreground = Brushes.White,
            FontSize = 22,
            Width = 44,
            Height = 44,
            Margin = new Thickness(6, 4, 6, 4),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(active ? AccentColor : Colors.Transparent),
            ToolTip = tooltip,
        };
        btn.Template = BuildIconButtonTemplate(active ? AccentColor : Colors.Transparent, IconHoverBg);
        return btn;
    }

    // ─── boards panel ────────────────────────────────────────────────────

    private Border BuildBoardsPanel()
    {
        var host = new Border
        {
            Background = new SolidColorBrush(BoardsPanelBg),
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(BorderColor),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = "Boards",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 14, 16, 10),
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // Scrollable list of board pills.
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _boardsList = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 0, 8, 0) };
        scroll.Content = _boardsList;
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var addBtn = BuildFlatButton("+ Novo board", AccentColor, AccentHoverColor, width: double.NaN, height: 34, fontSize: 12);
        addBtn.Margin = new Thickness(12, 8, 12, 12);
        addBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        addBtn.Click += (_, _) => CreateBoardPrompt();
        Grid.SetRow(addBtn, 2);
        grid.Children.Add(addBtn);

        host.Child = grid;
        return host;
    }

    private void RebuildBoardsList()
    {
        _boardsList.Children.Clear();
        var cfg = _library.Config;
        foreach (var b in cfg.Boards)
        {
            _boardsList.Children.Add(BuildBoardPill(b, isActive: b.Id == cfg.ActiveBoardId));
        }
        // Update the header in the main pane too so it tracks the active board.
        if (_boardHeaderName is not null)
        {
            var active = cfg.Boards.FirstOrDefault(b => b.Id == cfg.ActiveBoardId);
            _boardHeaderName.Text = active?.Name ?? "—";
        }
    }

    private Border BuildBoardPill(Board board, bool isActive)
    {
        var pillBg = isActive
            ? new SolidColorBrush(ParseColorOrFallback(board.Color))
            : new SolidColorBrush(PanelBg);
        var border = new Border
        {
            Margin = new Thickness(4, 3, 4, 3),
            CornerRadius = new CornerRadius(8),
            Background = pillBg,
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(isActive ? 0 : 1),
            BorderBrush = new SolidColorBrush(BorderColor),
        };
        var label = new TextBlock
        {
            Text = board.Name,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        border.Child = label;
        if (!isActive)
        {
            border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(IconHoverBg);
            border.MouseLeave += (_, _) => border.Background = new SolidColorBrush(PanelBg);
        }
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (board.Id == _library.Config.ActiveBoardId) return;
            try { _library.ActivateBoard(board.Id); }
            catch (Exception ex) { Console.Error.WriteLine($"activate board {board.Id}: {ex.Message}"); }
        };
        // Right-click → context menu (rename/recolor/delete).
        border.ContextMenu = BuildBoardContextMenu(board);
        return border;
    }

    private ContextMenu BuildBoardContextMenu(Board board)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Renomear" };
        rename.Click += (_, _) =>
        {
            var newName = TextPrompt.Ask(this, "Renomear board", "Nome:", board.Name);
            if (string.IsNullOrWhiteSpace(newName)) return;
            try { _library.UpdateBoard(board.Id, name: newName); }
            catch (Exception ex) { ShowError($"Falha ao renomear: {ex.Message}"); }
        };
        var recolor = new MenuItem { Header = "Mudar cor" };
        recolor.Click += (_, _) =>
        {
            var color = ColorPickerDialog.Pick(this, board.Color);
            if (color is null) return;
            try { _library.UpdateBoard(board.Id, color: color); }
            catch (Exception ex) { ShowError($"Falha ao mudar cor: {ex.Message}"); }
        };
        var delete = new MenuItem { Header = "Apagar board", Foreground = new SolidColorBrush(StopColor) };
        delete.Click += (_, _) =>
        {
            if (_library.Config.Boards.Count <= 1)
            {
                ShowError("Não é possível apagar o último board.");
                return;
            }
            var confirm = System.Windows.MessageBox.Show(
                $"Apagar o board \"{board.Name}\"? Os sons dele serão removidos da lista (os arquivos de áudio continuam na pasta).",
                "Reson — apagar board",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
            try { _library.DeleteBoard(board.Id); }
            catch (Exception ex) { ShowError($"Falha ao apagar: {ex.Message}"); }
        };
        menu.Items.Add(rename);
        menu.Items.Add(recolor);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        return menu;
    }

    private void CreateBoardPrompt()
    {
        var name = TextPrompt.Ask(this, "Novo board", "Nome do board:", "Novo board");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var b = _library.CreateBoard(name);
            _library.ActivateBoard(b.Id);
        }
        catch (Exception ex) { ShowError($"Falha ao criar board: {ex.Message}"); }
    }

    // ─── main pane (header + grid + bottom bar) ──────────────────────────

    private Grid BuildMainPane()
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // board header
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // grid
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // bottom bar

        var header = BuildBoardHeader();
        Grid.SetRow(header, 0);
        pane.Children.Add(header);

        _gridHost = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        Grid.SetRow(_gridHost, 1);
        pane.Children.Add(_gridHost);

        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 2);
        pane.Children.Add(bottom);

        return pane;
    }

    private Border BuildBoardHeader()
    {
        var host = new Border
        {
            Padding = new Thickness(20, 16, 20, 14),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(BorderColor),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _boardHeaderName = new TextBlock
        {
            Text = "—",
            Foreground = Brushes.White,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_boardHeaderName, 0);
        grid.Children.Add(_boardHeaderName);

        // "+" to add a sound to the active board.
        var addSoundBtn = BuildFlatButton("+ Som", AccentColor, AccentHoverColor, width: 90, height: 34, fontSize: 12);
        addSoundBtn.Margin = new Thickness(0, 0, 8, 0);
        addSoundBtn.Click += (_, _) => OpenAddSoundDialog();
        Grid.SetColumn(addSoundBtn, 1);
        grid.Children.Add(addSoundBtn);

        // 3-dot menu (rename / recolor / delete active board)
        var menuBtn = BuildFlatButton("…", NeutralColor, NeutralHoverColor, width: 36, height: 34, fontSize: 16);
        menuBtn.Click += (s, _) =>
        {
            var active = _library.Config.Boards.FirstOrDefault(b => b.Id == _library.Config.ActiveBoardId);
            if (active is null) return;
            var menu = BuildBoardContextMenu(active);
            menu.PlacementTarget = (Button)s!;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        Grid.SetColumn(menuBtn, 2);
        grid.Children.Add(menuBtn);

        host.Child = grid;
        return host;
    }

    private Border BuildBottomBar()
    {
        var bar = new Border
        {
            Background = new SolidColorBrush(PanelBg),
            Padding = new Thickness(20, 14, 20, 16),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(BorderColor),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stopButton = BuildStopButton();
        Grid.SetColumn(stopButton, 0);
        grid.Children.Add(stopButton);

        var volStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24, 0, 24, 0),
        };
        var volHeader = new DockPanel { LastChildFill = false };
        var volTitle = new TextBlock
        {
            Text = "Volume master",
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

        bar.Child = grid;
        return bar;
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

    // ─── sound grid (active board) ───────────────────────────────────────

    private void RebuildGrid()
    {
        _gridHost.Children.Clear();
        _gridHost.ColumnDefinitions.Clear();
        _gridHost.RowDefinitions.Clear();
        _tilesById.Clear();

        var active = _library.Config.Boards.FirstOrDefault(b => b.Id == _library.Config.ActiveBoardId);
        if (active is null) return;
        var cols = Math.Max(1, active.Grid.Cols);
        var rows = Math.Max(1, active.Grid.Rows);

        for (int c = 0; c < cols; c++)
            _gridHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++)
            _gridHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Render empty-cell outlines underneath the tiles. Clicking an empty
        // cell opens the AddSoundDialog at that target position.
        var occupied = new HashSet<GridPosition>();
        foreach (var s in active.Sounds.Where(s => s.Position is not null))
            occupied.Add(s.Position!);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var pos = new GridPosition(c, r);
                if (occupied.Contains(pos)) continue;
                var outline = BuildEmptyCell(pos);
                Grid.SetColumn(outline, c);
                Grid.SetRow(outline, r);
                _gridHost.Children.Add(outline);
            }
        }

        foreach (var sound in active.Sounds.Where(s => s.Position is not null))
        {
            if (sound.Position!.Col >= cols || sound.Position.Row >= rows) continue;
            var tile = BuildSoundTile(sound);
            Grid.SetColumn(tile, sound.Position.Col);
            Grid.SetRow(tile, sound.Position.Row);
            _gridHost.Children.Add(tile);
            _tilesById[sound.Id] = tile;
        }

        if (_currentlyPlayingId is not null && _tilesById.TryGetValue(_currentlyPlayingId, out var current))
            ApplyPlayingStyle(current, true);
    }

    private Border BuildEmptyCell(GridPosition pos)
    {
        var outline = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(BorderColor) { Opacity = 0.5 },
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Adicionar som aqui",
        };
        outline.MouseEnter += (_, _) => outline.Background = new SolidColorBrush(IconHoverBg) { Opacity = 0.3 };
        outline.MouseLeave += (_, _) => outline.Background = Brushes.Transparent;
        outline.MouseLeftButtonUp += (_, _) => OpenAddSoundDialog(pos);
        return outline;
    }

    private Border BuildSoundTile(SoundEntry sound)
    {
        var path = Path.Combine(_soundsDir, sound.File);
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
        border.Effect = new DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.35,
            Color = Colors.Black,
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(sound.Label) ? sound.Id : sound.Label,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        };
        stack.Children.Add(label);
        if (sound.Volume != 100)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"🔊 {sound.Volume}%",
                Foreground = new SolidColorBrush(Colors.White) { Opacity = 0.8 },
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        if (missing)
        {
            border.Opacity = 0.4;
            stack.Children.Add(new TextBlock
            {
                Text = "(arquivo ausente)",
                Foreground = Brushes.White,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        border.Child = stack;

        if (!missing)
        {
            border.MouseLeftButtonUp += (_, _) =>
            {
                try { _engine.Play(sound.Id, path); }
                catch (Exception ex) { Console.Error.WriteLine($"play {sound.Id}: {ex.Message}"); }
            };
            border.MouseEnter += (_, _) => border.Opacity = 0.92;
            border.MouseLeave += (_, _) => border.Opacity = 1.0;
        }

        border.ContextMenu = BuildSoundContextMenu(sound);
        return border;
    }

    private ContextMenu BuildSoundContextMenu(SoundEntry sound)
    {
        var menu = new ContextMenu();

        var edit = new MenuItem { Header = "Editar" };
        edit.Click += (_, _) => OpenEditSoundDialog(sound);
        menu.Items.Add(edit);

        var recolor = new MenuItem { Header = "Mudar cor" };
        recolor.Click += (_, _) =>
        {
            var color = ColorPickerDialog.Pick(this, sound.Color);
            if (color is null) return;
            try { _library.UpdateSound(sound.Id, color: color); }
            catch (Exception ex) { ShowError($"Falha ao mudar cor: {ex.Message}"); }
        };
        menu.Items.Add(recolor);

        var volume = new MenuItem { Header = "Volume..." };
        volume.Click += (_, _) =>
        {
            var v = VolumeDialog.Pick(this, sound.Volume);
            if (v is null) return;
            try { _library.UpdateSound(sound.Id, volume: v.Value); }
            catch (Exception ex) { ShowError($"Falha ao mudar volume: {ex.Message}"); }
        };
        menu.Items.Add(volume);

        // Move-to submenu — populated lazily so a new board appearing mid-life
        // shows up the next time the user opens the menu.
        var moveTo = new MenuItem { Header = "Mover para outro board" };
        moveTo.SubmenuOpened += (_, _) =>
        {
            moveTo.Items.Clear();
            foreach (var b in _library.Config.Boards.Where(b => b.Id != _library.Config.ActiveBoardId))
            {
                var item = new MenuItem { Header = b.Name };
                item.Click += (_, _) =>
                {
                    try { _library.MoveSoundToBoard(sound.Id, b.Id); }
                    catch (Exception ex) { ShowError($"Falha ao mover: {ex.Message}"); }
                };
                moveTo.Items.Add(item);
            }
            if (moveTo.Items.Count == 0)
                moveTo.Items.Add(new MenuItem { Header = "(nenhum outro board)", IsEnabled = false });
        };
        // Pre-seed once so the SubmenuOpened arrow shows on initial mount.
        moveTo.Items.Add(new MenuItem { Header = "...", IsEnabled = false });
        menu.Items.Add(moveTo);

        menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Apagar", Foreground = new SolidColorBrush(StopColor) };
        delete.Click += (_, _) =>
        {
            var confirm = System.Windows.MessageBox.Show(
                $"Apagar o som \"{sound.Label}\"? O arquivo de áudio também será removido da pasta.",
                "Reson — apagar som",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
            try { _library.DeleteSound(sound.Id, deleteFile: true); }
            catch (Exception ex) { ShowError($"Falha ao apagar: {ex.Message}"); }
        };
        menu.Items.Add(delete);

        return menu;
    }

    // ─── dialogs ─────────────────────────────────────────────────────────

    private void OpenAddSoundDialog(GridPosition? targetPosition = null)
    {
        var dlg = new AddSoundDialog(_library, _soundsDir, targetPosition) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenEditSoundDialog(SoundEntry sound)
    {
        var dlg = new EditSoundDialog(_library, sound) { Owner = this };
        dlg.ShowDialog();
    }

    private void OpenSettingsDialog()
    {
        try
        {
            var dlg = new SettingsWindow(_library, _locator, _port) { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex) { ShowError($"Não foi possível abrir as configurações:\n{ex.Message}"); }
    }

    private void ToggleConnectionPopover()
    {
        if (_connectionPopover is { IsOpen: true })
        {
            _connectionPopover.IsOpen = false;
            return;
        }
        _connectionPopover = new ConnectionPopover(_library, _adapter, _port)
        {
            PlacementTarget = _connectionIconBtn,
            Placement = PlacementMode.Right,
            StaysOpen = false,
        };
        _connectionPopover.IsOpen = true;
    }

    private void ShowError(string message)
    {
        System.Windows.MessageBox.Show(message, "Reson", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    // ─── playing-tile glow ───────────────────────────────────────────────

    private void ApplyPlayingStyle(Border tile, bool playing)
    {
        if (playing)
        {
            tile.BorderThickness = new Thickness(3);
            var glow = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 0,
                Color = Colors.White,
                Opacity = 0.6,
            };
            tile.Effect = glow;
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
            tile.BorderThickness = new Thickness(0);
            tile.Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.35,
                Color = Colors.Black,
            };
        }
    }

    // ─── engine event handlers ───────────────────────────────────────────

    private void OnPlayingUi(string id)
    {
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
        _suppressEngineEcho = true;
        try
        {
            _volumeSlider.Value = v;
            _volumeLabel.Text = $"{v}%";
        }
        finally { _suppressEngineEcho = false; }
    }

    private void OnMonitorChangedUi(bool b)
    {
        _suppressEngineEcho = true;
        try { _monitorCheck.IsChecked = b; }
        finally { _suppressEngineEcho = false; }
    }

    private void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEngineEcho) return;
        var v = (int)Math.Round(e.NewValue);
        _volumeLabel.Text = $"{v}%";
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

    // ─── lifecycle ───────────────────────────────────────────────────────

    private void OnClosingHideToTray(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnClosedUnsubscribe(object? sender, EventArgs e)
    {
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

    // ─── shared visual helpers (internal so the dialog classes can reuse) ─

    internal static Color ParseColorOrFallback(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return AccentColor; }
    }

    internal static Button BuildFlatButton(string label, Color rest, Color hover, double width, double height, double fontSize)
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

    internal static ControlTemplate BuildFlatButtonTemplate(Color rest, Color hover, double cornerRadius = 8)
    {
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

    private static ControlTemplate BuildIconButtonTemplate(Color rest, Color hover)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bg";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
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
}
