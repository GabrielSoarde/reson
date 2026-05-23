using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Soundpad.Wpf;

/// <summary>
/// Modal text prompt — replacement for Microsoft.VisualBasic.Interaction.InputBox
/// (which would pull in Microsoft.VisualBasic.dll just for one one-liner). Used
/// by the boards panel to ask for a board name. Returns null on Cancel.
/// </summary>
internal sealed class TextPrompt : Window
{
    private readonly TextBox _input;
    public string? Value { get; private set; }

    private TextPrompt(string title, string label, string initialValue)
    {
        Title = title;
        Width = 380;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(MainWindow.WindowBg);
        Foreground = Brushes.White;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        _input = new TextBox
        {
            Text = initialValue,
            FontSize = 14,
            Padding = new Thickness(6, 6, 6, 6),
            Background = new SolidColorBrush(MainWindow.PanelBg),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(MainWindow.BorderColor),
            CaretBrush = Brushes.White,
        };
        Grid.SetRow(_input, 1);
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = MainWindow.BuildFlatButton("Cancelar", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, width: 100, height: 32, fontSize: 12);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = MainWindow.BuildFlatButton("OK", MainWindow.AccentColor, MainWindow.AccentHoverColor, width: 100, height: 32, fontSize: 12);
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            Value = _input.Text.Trim();
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    public static string? Ask(Window owner, string title, string label, string initialValue = "")
    {
        var dlg = new TextPrompt(title, label, initialValue) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
