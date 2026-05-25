using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Soundpad.Models;
using Soundpad.Sound;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Path = System.IO.Path;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Soundpad.Wpf;

/// <summary>
/// "Add sound" modal — preview tile on the left, label/file/color/volume form
/// on the right. Adds the picked sound to the active board: the file is copied
/// into the sounds/ folder (with collision-safe renaming) and AutoScan then
/// UpdateSound apply the form fields.
/// </summary>
internal sealed class AddSoundDialog : Window
{
    private readonly SoundLibrary _library;
    private readonly string _soundsDir;
    private readonly GridPosition? _targetPosition;

    private readonly TextBox _labelInput;
    private readonly TextBlock _filePathText;
    private readonly TextBox _colorInput;
    private readonly Slider _volumeSlider;
    private readonly TextBlock _volumeReadout;
    private readonly Border _previewTile;
    private readonly TextBlock _previewLabel;
    private readonly Button _addButton;
    private string? _pickedFile;

    public AddSoundDialog(SoundLibrary library, string soundsDir, GridPosition? targetPosition = null)
    {
        _library = library;
        _soundsDir = soundsDir;
        _targetPosition = targetPosition;

        Title = "Reson — adicionar som";
        Width = 620;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(MainWindow.WindowBg);
        Foreground = Brushes.White;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var root = new Grid { Margin = new Thickness(20) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Left: preview tile
        _previewTile = new Border
        {
            Margin = new Thickness(0, 0, 16, 0),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(MainWindow.AccentColor),
            Width = 200,
            Height = 200,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _previewLabel = new TextBlock
        {
            Text = "Novo som",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12),
        };
        _previewTile.Child = _previewLabel;
        Grid.SetColumn(_previewTile, 0);
        Grid.SetRow(_previewTile, 0);
        root.Children.Add(_previewTile);

        // Right: form
        var form = new StackPanel { Orientation = Orientation.Vertical };

        form.Children.Add(new TextBlock
        {
            Text = "Rótulo",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 0, 0, 4),
        });
        _labelInput = MakeTextBox("Novo som");
        _labelInput.MaxLength = 40;
        _labelInput.TextChanged += (_, _) => _previewLabel.Text = _labelInput.Text;
        form.Children.Add(_labelInput);

        form.Children.Add(new TextBlock
        {
            Text = "Arquivo de áudio",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 12, 0, 4),
        });
        var fileRow = new DockPanel { LastChildFill = true };
        var pickBtn = MainWindow.BuildFlatButton("Escolher...", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 110, 32, 12);
        pickBtn.Click += (_, _) => PickFile();
        DockPanel.SetDock(pickBtn, Dock.Right);
        _filePathText = new TextBlock
        {
            Text = "(nenhum)",
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        fileRow.Children.Add(pickBtn);
        fileRow.Children.Add(_filePathText);
        form.Children.Add(fileRow);

        form.Children.Add(new TextBlock
        {
            Text = "Cor (hex)",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 12, 0, 4),
        });
        var colorRow = new DockPanel { LastChildFill = true };
        // Bind _colorInput before constructing pickColorBtn so the lambda
        // captures a non-null reference.
        var initialColor = Soundpad.Sound.SoundColors.PickInitial(_library.ActiveBoard.Sounds.Count);
        _colorInput = MakeTextBox(initialColor);
        _colorInput.Margin = new Thickness(0, 0, 8, 0);
        _previewTile.Background = new SolidColorBrush(MainWindow.ParseColorOrFallback(initialColor));
        _colorInput.TextChanged += (_, _) =>
        {
            try { _previewTile.Background = new SolidColorBrush(MainWindow.ParseColorOrFallback(_colorInput.Text)); }
            catch { /* live preview is best-effort */ }
        };
        var pickColorBtn = MainWindow.BuildFlatButton("Palheta", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 90, 32, 12);
        pickColorBtn.Click += (_, _) =>
        {
            var c = ColorPickerDialog.Pick(this, _colorInput.Text);
            if (c is not null)
            {
                _colorInput.Text = c;
            }
        };
        DockPanel.SetDock(pickColorBtn, Dock.Right);
        colorRow.Children.Add(pickColorBtn);
        colorRow.Children.Add(_colorInput);
        form.Children.Add(colorRow);

        form.Children.Add(new TextBlock
        {
            Text = "Volume",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 12, 0, 4),
        });
        var volumeRow = new DockPanel { LastChildFill = true };
        _volumeReadout = new TextBlock
        {
            Text = "100%",
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 50,
            TextAlignment = TextAlignment.Right,
        };
        DockPanel.SetDock(_volumeReadout, Dock.Right);
        _volumeSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _volumeSlider.ValueChanged += (_, _) => _volumeReadout.Text = $"{(int)Math.Round(_volumeSlider.Value)}%";
        volumeRow.Children.Add(_volumeReadout);
        volumeRow.Children.Add(_volumeSlider);
        form.Children.Add(volumeRow);

        Grid.SetColumn(form, 1);
        Grid.SetRow(form, 0);
        root.Children.Add(form);

        // Bottom buttons row spanning both columns
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var cancel = MainWindow.BuildFlatButton("Cancelar", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 100, 34, 12);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _addButton = MainWindow.BuildFlatButton("ADICIONAR", MainWindow.AccentColor, MainWindow.AccentHoverColor, 130, 34, 12);
        _addButton.Margin = new Thickness(8, 0, 0, 0);
        _addButton.IsDefault = true;
        _addButton.IsEnabled = false;
        _addButton.Click += (_, _) => Commit();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_addButton);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        Content = root;
    }

    private TextBox MakeTextBox(string initial)
    {
        return new TextBox
        {
            Text = initial,
            FontSize = 13,
            Padding = new Thickness(6, 6, 6, 6),
            Background = new SolidColorBrush(MainWindow.PanelBg),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(MainWindow.BorderColor),
            CaretBrush = Brushes.White,
        };
    }

    private void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolher arquivo de áudio",
            Multiselect = false,
            Filter = "Audio (*.mp3;*.wav;*.ogg;*.flac)|*.mp3;*.wav;*.ogg;*.flac|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        _pickedFile = dlg.FileName;
        _filePathText.Text = Path.GetFileName(_pickedFile);
        _addButton.IsEnabled = true;
        // Auto-fill the label from the filename stem if the user hasn't
        // touched it yet — small QoL win.
        if (_labelInput.Text == "Novo som" || string.IsNullOrWhiteSpace(_labelInput.Text))
        {
            _labelInput.Text = Path.GetFileNameWithoutExtension(_pickedFile);
        }
    }

    private void Commit()
    {
        if (string.IsNullOrEmpty(_pickedFile) || !File.Exists(_pickedFile))
        {
            System.Windows.MessageBox.Show("Selecione um arquivo de áudio primeiro.", "Reson");
            return;
        }
        var ext = Path.GetExtension(_pickedFile).ToLowerInvariant();
        if (ext is not (".mp3" or ".wav" or ".ogg" or ".flac"))
        {
            System.Windows.MessageBox.Show("Extensão não suportada. Use mp3, wav, ogg ou flac.", "Reson");
            return;
        }

        try
        {
            // Copy + register through the standard pipeline. We open a
            // FileStream and run it through SoundLibrary.Upload — same path
            // the HTTP /api/sounds/upload endpoint uses, so collision
            // handling, id derivation, grid placement, and Changed events
            // all flow identically.
            SoundEntry entry;
            using (var fs = File.OpenRead(_pickedFile))
            {
                var result = _library.Upload(Path.GetFileName(_pickedFile), fs);
                entry = result.Entry;
            }

            // Apply the form's label/color/volume + target position. Volume
            // defaults to 100 (no attenuation) so the conditional UpdateSound
            // call is just an optimization for the common case.
            var label = _labelInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = entry.Label;
            var color = _colorInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(color)) color = "#3b82f6";
            var volume = (int)Math.Round(_volumeSlider.Value);
            _library.UpdateSound(entry.Id, label: label, color: color, volume: volume,
                position: _targetPosition);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Falha ao adicionar som:\n{ex.Message}", "Reson",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
