using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soundpad.Audio;
using Soundpad.Sound;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Soundpad.Wpf;

/// <summary>
/// Modal device-picker dialog. Shows the current game/mic/monitor selections
/// and applies changes by POSTing to the same in-process HTTP API the phone UI
/// uses. We deliberately go through HTTP (instead of poking PlaybackEngine
/// directly) so the broadcast + auth + loop-guard logic stays in one place.
///
/// <para>Threading: lives on the WPF Dispatcher. HttpClient calls are awaited
/// with the default sync-context capture so UI updates after the await stay on
/// the dispatcher.</para>
///
/// <para>Echo filter: each Apply ships an X-Origin-Id header so the WPF window
/// (which also listens on the WebSocket) can recognize its own broadcast and
/// skip the redundant re-render. The id is generated once per dialog session.</para>
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const string NullMonitorSentinel = "(Desativado)";
    private const string DefaultMicSentinel = "(Padrão do Windows)";

    private static readonly Color BgColor = (Color)ColorConverter.ConvertFromString("#1f2937");
    private static readonly Color PanelColor = (Color)ColorConverter.ConvertFromString("#111827");
    private static readonly Color MutedTextColor = (Color)ColorConverter.ConvertFromString("#cbd5e1");
    private static readonly Color AccentColor = (Color)ColorConverter.ConvertFromString("#22c55e");
    private static readonly Color AccentHoverColor = (Color)ColorConverter.ConvertFromString("#16a34a");
    private static readonly Color CancelColor = (Color)ColorConverter.ConvertFromString("#374151");
    private static readonly Color CancelHoverColor = (Color)ColorConverter.ConvertFromString("#4b5563");

    private readonly SoundLibrary _library;
    private readonly DeviceLocator _locator;
    private readonly int _port;
    private readonly string _originId;

    // Captured "initial" values so Apply can diff and only POST what changed.
    private readonly string? _initialGameId;
    private readonly string? _initialMonitorId;
    private readonly string? _initialMicId;
    private readonly bool _initialMonitorEnabled;

    // Controls — populated in BuildLayout.
    private ComboBox _gameCombo = null!;
    private ComboBox _monitorCombo = null!;
    private ComboBox _micCombo = null!;
    private CheckBox _monitorEnabledCheck = null!;
    private Button _applyButton = null!;
    private Button _cancelButton = null!;

    // Snapshot of currently-enumerated devices so the picker's selection maps
    // unambiguously to an id even if two devices happen to share a FriendlyName.
    // Key = display label as it appears in the dropdown; Value = endpoint id.
    private readonly Dictionary<string, string> _gameOptions = new();
    private readonly Dictionary<string, string> _monitorOptions = new();
    private readonly Dictionary<string, string> _micOptions = new();

    public SettingsWindow(SoundLibrary library, DeviceLocator locator, int port)
    {
        _library = library;
        _locator = locator;
        _port = port;
        _originId = Guid.NewGuid().ToString("N");

        var cfg = _library.Config;
        _initialGameId = cfg.AudioDevice;
        _initialMonitorId = cfg.MonitorDevice;
        _initialMicId = cfg.MicDevice;
        _initialMonitorEnabled = cfg.MonitorEnabled;

        Title = "Reson — Configurações";
        Width = 560;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(BgColor);
        Foreground = Brushes.White;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        // Reson icon on the settings dialog title bar too — consistent with
        // MainWindow and avoids a generic alt-tab thumbnail.
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Reson.ico", UriKind.Absolute));
        }
        catch { /* fall back to default WPF icon */ }

        BuildLayout();
        PopulateDevices();
    }

    private void BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // sections
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

        var title = new TextBlock
        {
            Text = "Dispositivos de áudio",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 16),
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var stack = new StackPanel { Orientation = Orientation.Vertical };

        // Game device (mandatory for sound output to Discord/Valorant).
        _gameCombo = NewCombo();
        stack.Children.Add(BuildSection(
            "Dispositivo de saída (Discord/Valorant)",
            "O Reson envia o som para este dispositivo. Use um cabo virtual (VB-Cable / VoiceMeeter) para que o Discord o escute como microfone.",
            _gameCombo));

        // Mic device.
        _micCombo = NewCombo();
        stack.Children.Add(BuildSection(
            "Dispositivo do microfone",
            "Sua voz é mixada junto com os sons. Use \"" + DefaultMicSentinel + "\" para seguir a configuração do Windows.",
            _micCombo));

        // Monitor device + enable checkbox.
        _monitorCombo = NewCombo();
        _monitorEnabledCheck = new CheckBox
        {
            Content = "Habilitar monitor (você ouve os sons no seu fone)",
            Foreground = Brushes.White,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            IsChecked = _initialMonitorEnabled,
        };
        var monitorSection = BuildSection(
            "Dispositivo de monitor (seu fone)",
            "Escolha onde você quer ouvir os sons localmente. Use \"" + NullMonitorSentinel + "\" para desligar.",
            _monitorCombo);
        ((StackPanel)monitorSection.Child).Children.Add(_monitorEnabledCheck);
        stack.Children.Add(monitorSection);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Buttons row (Cancel | Apply, right-aligned).
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        _cancelButton = BuildButton("Cancelar", CancelColor, CancelHoverColor);
        _cancelButton.Click += (_, _) => { DialogResult = false; Close(); };
        _applyButton = BuildButton("Aplicar", AccentColor, AccentHoverColor);
        _applyButton.Margin = new Thickness(8, 0, 0, 0);
        _applyButton.Click += async (_, _) => await ApplyAsync();
        buttons.Children.Add(_cancelButton);
        buttons.Children.Add(_applyButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }

    private static ComboBox NewCombo() => new()
    {
        Margin = new Thickness(0, 6, 0, 0),
        Padding = new Thickness(6, 4, 6, 4),
        FontSize = 13,
        Height = 28,
    };

    private static Border BuildSection(string title, string subtitle, ComboBox combo)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = new SolidColorBrush(MutedTextColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        stack.Children.Add(combo);
        return new Border
        {
            Background = new SolidColorBrush(PanelColor),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack,
        };
    }

    private static Button BuildButton(string text, Color rest, Color hover)
    {
        var btn = new Button
        {
            Content = text,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Width = 110,
            Height = 34,
            Background = new SolidColorBrush(rest),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        // Reuse the flat template logic from MainWindow via an inline copy —
        // dialog is small enough that we don't extract a shared helper.
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Bg";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(rest));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(hover), "Bg"));
        template.Triggers.Add(hoverTrigger);
        btn.Template = template;
        return btn;
    }

    /// <summary>
    /// Populate the three dropdowns from the current device enumeration. We
    /// keep the option map (label → id) alongside so Apply can resolve the
    /// user's choice back to a persisted endpoint id without re-querying.
    /// </summary>
    private void PopulateDevices()
    {
        IReadOnlyList<AudioDeviceInfo> outs;
        IReadOnlyList<AudioDeviceInfo> ins;
        try { outs = _locator.EnumerateRenderDevices(); } catch { outs = Array.Empty<AudioDeviceInfo>(); }
        try { ins = _locator.EnumerateCaptureDevices(); } catch { ins = Array.Empty<AudioDeviceInfo>(); }

        // Game (output): no sentinel — null game device disables playback entirely
        // which is a confusing UX. Allow it implicitly only when there are zero
        // devices (combo stays empty/disabled).
        _gameOptions.Clear();
        _gameCombo.Items.Clear();
        foreach (var d in outs)
        {
            var label = DisambiguateLabel(_gameOptions, d.FriendlyName);
            _gameOptions[label] = d.Id;
            _gameCombo.Items.Add(label);
        }
        _gameCombo.IsEnabled = outs.Count > 0;
        if (_initialGameId is not null)
        {
            var match = _gameOptions.FirstOrDefault(kv => string.Equals(kv.Value, _initialGameId, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null) _gameCombo.SelectedItem = match.Key;
        }
        else if (outs.Count > 0)
        {
            _gameCombo.SelectedIndex = 0;
        }

        // Monitor: sentinel "(Desativado)" → null id.
        _monitorOptions.Clear();
        _monitorCombo.Items.Clear();
        _monitorCombo.Items.Add(NullMonitorSentinel);
        foreach (var d in outs)
        {
            var label = DisambiguateLabel(_monitorOptions, d.FriendlyName);
            _monitorOptions[label] = d.Id;
            _monitorCombo.Items.Add(label);
        }
        if (_initialMonitorId is not null)
        {
            var match = _monitorOptions.FirstOrDefault(kv => string.Equals(kv.Value, _initialMonitorId, StringComparison.OrdinalIgnoreCase));
            _monitorCombo.SelectedItem = match.Key ?? NullMonitorSentinel;
        }
        else
        {
            _monitorCombo.SelectedItem = NullMonitorSentinel;
        }

        // Mic: sentinel "(Padrão do Windows)" → null id (engine uses system default).
        _micOptions.Clear();
        _micCombo.Items.Clear();
        _micCombo.Items.Add(DefaultMicSentinel);
        foreach (var d in ins)
        {
            var label = DisambiguateLabel(_micOptions, d.FriendlyName);
            _micOptions[label] = d.Id;
            _micCombo.Items.Add(label);
        }
        if (_initialMicId is not null)
        {
            var match = _micOptions.FirstOrDefault(kv => string.Equals(kv.Value, _initialMicId, StringComparison.OrdinalIgnoreCase));
            _micCombo.SelectedItem = match.Key ?? DefaultMicSentinel;
        }
        else
        {
            _micCombo.SelectedItem = DefaultMicSentinel;
        }
    }

    /// <summary>
    /// Two devices can share a FriendlyName (e.g. two identical headsets), so
    /// append " (2)", " (3)", etc. to keep dropdown labels unique. The visible
    /// difference is enough for the user to pick, and the id map preserves
    /// the actual selection.
    /// </summary>
    private static string DisambiguateLabel(Dictionary<string, string> existing, string baseName)
    {
        if (!existing.ContainsKey(baseName)) return baseName;
        for (int i = 2; i < 100; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!existing.ContainsKey(candidate)) return candidate;
        }
        return baseName + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
    }

    /// <summary>
    /// Diff the selection against the captured initial values and POST only
    /// the endpoints that changed. Errors are surfaced via MessageBox and
    /// the dialog stays open so the user can correct. On full success, close.
    /// </summary>
    private async Task ApplyAsync()
    {
        _applyButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        try
        {
            string? newGameId = ResolveSelection(_gameCombo, _gameOptions, NullMonitorSentinel, _initialGameId);
            string? newMonitorId = ResolveSelection(_monitorCombo, _monitorOptions, NullMonitorSentinel, _initialMonitorId);
            string? newMicId = ResolveSelection(_micCombo, _micOptions, DefaultMicSentinel, _initialMicId);
            bool newMonitorEnabled = _monitorEnabledCheck.IsChecked == true;

            using var http = BuildHttpClient();

            // Order matters: changing the game device first means the monitor's
            // loop-guard check (game != monitor) sees the new game device. If
            // the user swaps game↔monitor we'd otherwise reject the first half
            // of a valid pair. We POST monitor as null first to vacate the
            // potential conflict, then game, then monitor's real value, then mic.
            // This is overkill for the common case (one field changed) but
            // robust against the swap.
            var gameChanged = !string.Equals(newGameId, _initialGameId, StringComparison.OrdinalIgnoreCase);
            var monitorChanged = !string.Equals(newMonitorId, _initialMonitorId, StringComparison.OrdinalIgnoreCase);
            var monitorEnabledChanged = newMonitorEnabled != _initialMonitorEnabled;
            var micChanged = !string.Equals(newMicId, _initialMicId, StringComparison.OrdinalIgnoreCase);

            if (!(gameChanged || monitorChanged || monitorEnabledChanged || micChanged))
            {
                DialogResult = true;
                Close();
                return;
            }

            // Step 1: if both game and monitor changed, drop monitor first so
            // the upcoming game POST doesn't trip the loop guard.
            if (gameChanged && monitorChanged && _initialMonitorId is not null)
            {
                if (!await PostDeviceAsync(http, "/api/monitor/device", null)) return;
            }

            if (gameChanged)
            {
                if (!await PostDeviceAsync(http, "/api/game/device", newGameId)) return;
            }

            if (monitorChanged)
            {
                if (!await PostDeviceAsync(http, "/api/monitor/device", newMonitorId)) return;
            }

            if (monitorEnabledChanged)
            {
                if (!await PostMonitorEnabledAsync(http, newMonitorEnabled)) return;
            }

            if (micChanged)
            {
                if (!await PostDeviceAsync(http, "/api/mic/device", newMicId)) return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Falha ao aplicar configurações:\n" + ex.Message,
                "Reson",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _applyButton.IsEnabled = true;
            _cancelButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Translate the picker's selected label to the persisted endpoint id.
    /// If the user picked the "null" sentinel for monitor/mic, return null.
    /// If the picker is empty (no devices available), fall back to the
    /// initial value so we don't accidentally write null over a real device.
    /// </summary>
    private static string? ResolveSelection(ComboBox combo, Dictionary<string, string> map, string nullSentinel, string? fallback)
    {
        var selected = combo.SelectedItem as string;
        if (selected is null) return fallback;
        if (selected == nullSentinel) return null;
        return map.TryGetValue(selected, out var id) ? id : fallback;
    }

    private HttpClient BuildHttpClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}/"),
            Timeout = TimeSpan.FromSeconds(8),
        };
        http.DefaultRequestHeaders.Add("X-Auth-Token", _library.Config.AuthToken);
        http.DefaultRequestHeaders.Add("X-Origin-Id", _originId);
        return http;
    }

    private async Task<bool> PostDeviceAsync(HttpClient http, string path, string? device)
    {
        try
        {
            var res = await http.PostAsJsonAsync(path, new { device });
            if (res.IsSuccessStatusCode) return true;
            var body = await res.Content.ReadAsStringAsync();
            ShowApiError(path, res.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Erro de rede ao chamar {path}:\n{ex.Message}",
                "Reson",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    private async Task<bool> PostMonitorEnabledAsync(HttpClient http, bool enabled)
    {
        try
        {
            var res = await http.PostAsJsonAsync("/api/monitor", new { enabled });
            if (res.IsSuccessStatusCode) return true;
            var body = await res.Content.ReadAsStringAsync();
            ShowApiError("/api/monitor", res.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Erro de rede ao chamar /api/monitor:\n{ex.Message}",
                "Reson",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// Translate API error codes into actionable PT-BR messages. The two
    /// loop-guard codes are the most likely user-triggered failures, so we
    /// give them a tailored message instead of dumping JSON.
    /// </summary>
    private static void ShowApiError(string path, System.Net.HttpStatusCode status, string body)
    {
        var message = (status, body) switch
        {
            (System.Net.HttpStatusCode.BadRequest, var b) when b.Contains("monitor_equals_game_device") =>
                "O dispositivo de monitor não pode ser igual ao dispositivo de saída do jogo (causaria eco).",
            (System.Net.HttpStatusCode.BadRequest, var b) when b.Contains("game_equals_monitor_device") =>
                "O dispositivo de saída do jogo não pode ser igual ao dispositivo de monitor (causaria eco).",
            (System.Net.HttpStatusCode.BadRequest, var b) when b.Contains("unknown_device") =>
                "Dispositivo não reconhecido — talvez ele tenha sido desconectado. Feche e reabra as configurações.",
            (System.Net.HttpStatusCode.Unauthorized, _) =>
                "Falha de autenticação ao acessar a API local. Reinicie o Reson.",
            _ => $"Erro ({(int)status}) em {path}:\n{body}",
        };
        System.Windows.MessageBox.Show(
            message,
            "Reson",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }
}
