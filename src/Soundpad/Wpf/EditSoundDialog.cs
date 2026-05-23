using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Soundpad.Models;
using Soundpad.Sound;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Soundpad.Wpf;

/// <summary>
/// "Edit sound" modal — same layout as <see cref="AddSoundDialog"/> minus the
/// file picker (you can't swap the underlying audio file without re-uploading).
/// Pre-fills with the existing sound's fields and applies updates via
/// <see cref="SoundLibrary.UpdateSound"/>.
/// </summary>
internal sealed class EditSoundDialog : Window
{
    private readonly SoundLibrary _library;
    private readonly SoundEntry _sound;

    private readonly TextBox _labelInput;
    private readonly TextBox _colorInput;
    private readonly Slider _volumeSlider;
    private readonly TextBlock _volumeReadout;
    private readonly Border _previewTile;
    private readonly TextBlock _previewLabel;

    public EditSoundDialog(SoundLibrary library, SoundEntry sound)
    {
        _library = library;
        _sound = sound;

        Title = "Reson — editar som";
        Width = 620;
        Height = 380;
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

        _previewTile = new Border
        {
            Margin = new Thickness(0, 0, 16, 0),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(MainWindow.ParseColorOrFallback(sound.Color)),
            Width = 200,
            Height = 200,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _previewLabel = new TextBlock
        {
            Text = sound.Label,
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

        var form = new StackPanel { Orientation = Orientation.Vertical };
        form.Children.Add(new TextBlock
        {
            Text = "Rótulo",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 0, 0, 4),
        });
        _labelInput = MakeTextBox(sound.Label);
        _labelInput.TextChanged += (_, _) => _previewLabel.Text = _labelInput.Text;
        form.Children.Add(_labelInput);

        form.Children.Add(new TextBlock
        {
            Text = "Cor (hex)",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 12, 0, 4),
        });
        var colorRow = new DockPanel { LastChildFill = true };
        // Bind _colorInput before the pickColorBtn click handler captures it
        // so the nullable-flow analyzer is happy.
        _colorInput = MakeTextBox(sound.Color);
        _colorInput.Margin = new Thickness(0, 0, 8, 0);
        _colorInput.TextChanged += (_, _) =>
        {
            try { _previewTile.Background = new SolidColorBrush(MainWindow.ParseColorOrFallback(_colorInput.Text)); }
            catch { /* live preview is best-effort */ }
        };
        var pickColorBtn = MainWindow.BuildFlatButton("Palheta", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 90, 32, 12);
        pickColorBtn.Click += (_, _) =>
        {
            var c = ColorPickerDialog.Pick(this, _colorInput.Text);
            if (c is not null) _colorInput.Text = c;
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
            Text = $"{sound.Volume}%",
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
            Value = sound.Volume,
            TickFrequency = 10,
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

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var cancel = MainWindow.BuildFlatButton("Cancelar", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 100, 34, 12);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MainWindow.BuildFlatButton("SALVAR", MainWindow.AccentColor, MainWindow.AccentHoverColor, 130, 34, 12);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        Content = root;
    }

    private TextBox MakeTextBox(string initial) => new()
    {
        Text = initial,
        FontSize = 13,
        Padding = new Thickness(6, 6, 6, 6),
        Background = new SolidColorBrush(MainWindow.PanelBg),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(MainWindow.BorderColor),
        CaretBrush = Brushes.White,
    };

    private void Commit()
    {
        try
        {
            var label = _labelInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(label)) label = _sound.Label;
            var color = _colorInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(color)) color = _sound.Color;
            var volume = (int)Math.Round(_volumeSlider.Value);
            _library.UpdateSound(_sound.Id, label: label, color: color, volume: volume);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Falha ao salvar:\n{ex.Message}", "Reson",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
