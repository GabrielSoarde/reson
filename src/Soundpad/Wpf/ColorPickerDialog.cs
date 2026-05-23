using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Soundpad.Wpf;

/// <summary>
/// Tiny color picker dialog — a grid of palette swatches plus a free-form hex
/// input. Returns the chosen hex string (e.g. "#3b82f6") or null on Cancel.
/// Deliberately avoids the WinForms System.Windows.Forms.ColorDialog because
/// its native chrome clashes with the Reson dark theme.
/// </summary>
internal sealed class ColorPickerDialog : Window
{
    // Curated palette — matches the Deckboard tile palette so a sound's color
    // visually maps to a "category" without forcing the user to mix RGB.
    private static readonly string[] Palette =
    {
        "#3b82f6", "#22c55e", "#ef4444", "#f59e0b",
        "#a855f7", "#06b6d4", "#ec4899", "#84cc16",
        "#f97316", "#14b8a6", "#8b5cf6", "#64748b",
    };

    private readonly TextBox _hexInput;
    public string? Result { get; private set; }

    private ColorPickerDialog(string initial)
    {
        Title = "Reson — escolher cor";
        Width = 320;
        Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(MainWindow.WindowBg);
        Foreground = Brushes.White;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        // Bind the hex input upfront so the swatch click handlers below
        // capture a non-null reference (nullable-flow analysis).
        _hexInput = new TextBox
        {
            Text = initial,
            Padding = new Thickness(6, 6, 6, 6),
            FontSize = 13,
            Background = new SolidColorBrush(MainWindow.PanelBg),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(MainWindow.BorderColor),
            CaretBrush = Brushes.White,
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Palheta",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var paletteGrid = new UniformGrid { Columns = 6, Rows = 2 };
        foreach (var hex in Palette)
        {
            var swatch = new System.Windows.Shapes.Rectangle
            {
                Width = 36,
                Height = 36,
                Margin = new Thickness(4),
                Fill = new SolidColorBrush(MainWindow.ParseColorOrFallback(hex)),
                RadiusX = 6,
                RadiusY = 6,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            swatch.MouseLeftButtonUp += (_, _) => { _hexInput.Text = hex; };
            paletteGrid.Children.Add(swatch);
        }
        Grid.SetRow(paletteGrid, 1);
        root.Children.Add(paletteGrid);

        var hexLabel = new TextBlock
        {
            Text = "Hex personalizado",
            FontSize = 12,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            Margin = new Thickness(0, 12, 0, 6),
        };
        Grid.SetRow(hexLabel, 2);
        root.Children.Add(hexLabel);

        Grid.SetRow(_hexInput, 3);
        root.Children.Add(_hexInput);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var cancel = MainWindow.BuildFlatButton("Cancelar", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 100, 32, 12);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = MainWindow.BuildFlatButton("OK", MainWindow.AccentColor, MainWindow.AccentHoverColor, 100, 32, 12);
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            var hex = (_hexInput.Text ?? string.Empty).Trim();
            if (!hex.StartsWith('#')) hex = "#" + hex;
            try
            {
                _ = (Color)ColorConverter.ConvertFromString(hex);
            }
            catch { System.Windows.MessageBox.Show("Cor inválida.", "Reson"); return; }
            Result = hex;
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 4);
        root.Children.Add(buttons);

        Content = root;
    }

    public static string? Pick(Window owner, string initial)
    {
        var dlg = new ColorPickerDialog(initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
