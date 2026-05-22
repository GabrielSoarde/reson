using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Soundpad.Audio;
using Soundpad.Models;
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
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Soundpad.Wpf;

/// <summary>
/// Native Soundpad window — the PC twin of the phone web UI.
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
    private readonly int _port;
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

    private Grid _gridHost = null!;
    private Slider _volumeSlider = null!;
    private TextBlock _volumeLabel = null!;
    private CheckBox _monitorCheck = null!;
    private string? _currentlyPlayingId;
    // Map sound id → its visual root (Border) so we can flip the "playing" style fast.
    private readonly Dictionary<string, SoundTile> _tilesById = new();
    private bool _suppressEngineEcho;  // ignore the next VolumeChanged when we caused it

    public MainWindow(SoundLibrary library, PlaybackEngine engine, DeviceLocator locator, int port)
    {
        _library = library;
        _engine = engine;
        _locator = locator;
        _port = port;

        Title = "Soundpad";
        Width = 900;
        Height = 650;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(BgColor);
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Brushes.White;
        UseLayoutRounding = true;

        BuildLayout();
        RebuildGrid();

        _onPlaying = id => Dispatcher.BeginInvoke(new Action(() => OnPlayingUi(id)));
        _onStopped = () => Dispatcher.BeginInvoke(new Action(OnStoppedUi));
        _onVolumeChanged = v => Dispatcher.BeginInvoke(new Action(() => OnVolumeChangedUi(v)));
        _onMonitorChanged = b => Dispatcher.BeginInvoke(new Action(() => OnMonitorChangedUi(b)));
        _onLibraryChanged = () => Dispatcher.BeginInvoke(new Action(RebuildGrid));

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
                "Soundpad — escolha um dispositivo",
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
            "O Soundpad precisa de VB-Cable ou VoiceMeeter instalado para enviar som ao Discord/Valorant.\n\n" +
            "Deseja abrir a página de download do VB-Cable agora?",
            "Soundpad — dispositivo de áudio ausente",
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
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // grid
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // bottom bar

        // Header (title strip)
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
            Text = "Soundpad",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        headerStack.Children.Add(dot);
        headerStack.Children.Add(titleText);
        header.Child = headerStack;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Grid host (sound buttons live here)
        _gridHost = new Grid
        {
            Margin = new Thickness(20, 20, 20, 12),
        };
        Grid.SetRow(_gridHost, 1);
        root.Children.Add(_gridHost);

        // Bottom controls bar
        var bottom = BuildBottomBar();
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);

        Content = root;
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

        // Settings button (gear) — opens the device picker modal.
        var settingsBtn = BuildSettingsButton();
        Grid.SetColumn(settingsBtn, 3);
        grid.Children.Add(settingsBtn);

        bar.Child = grid;
        return bar;
    }

    private Button BuildSettingsButton()
    {
        // Plain "Configurações" text button — gear glyph (⚙) renders unevenly
        // across Segoe UI variants, so use text to stay readable on every box.
        var rest = (Color)ColorConverter.ConvertFromString("#374151");
        var hover = (Color)ColorConverter.ConvertFromString("#4b5563");
        var btn = new Button
        {
            Content = "⚙ Configurações",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Width = 150,
            Height = 36,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(rest),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        btn.Template = BuildFlatButtonTemplate(rest, hover);
        btn.Click += (_, _) => OpenSettingsDialog();
        return btn;
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
                "Soundpad",
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

    private static ControlTemplate BuildFlatButtonTemplate(Color rest, Color hover)
    {
        // We can't pass colors into a ControlTemplate easily without static
        // resources, so build the template per-instance with literal brushes
        // via a FrameworkElementFactory.
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

        var soundsDir = Path.Combine(AppContext.BaseDirectory, "sounds");
        foreach (var sound in config.Sounds.Where(s => s.Position is not null))
        {
            // Skip cells that fell off after a grid shrink.
            if (sound.Position!.Col >= cols || sound.Position.Row >= rows) continue;
            var tile = BuildSoundTile(sound, soundsDir);
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
