using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Soundpad.Wpf;

/// <summary>
/// Single-slider modal for picking a per-sound volume in 0-100. Used by the
/// sound tile context menu. Returns the picked value or null on Cancel.
/// </summary>
internal sealed class VolumeDialog : Window
{
    private readonly Slider _slider;
    private readonly TextBlock _readout;
    public int? Result { get; private set; }

    private VolumeDialog(int initial)
    {
        Title = "Reson — volume do som";
        Width = 360;
        Height = 200;
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

        var heading = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        var label = new TextBlock
        {
            Text = "Volume deste som",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        };
        DockPanel.SetDock(label, Dock.Left);
        _readout = new TextBlock
        {
            Text = $"{initial}%",
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        };
        DockPanel.SetDock(_readout, Dock.Right);
        heading.Children.Add(label);
        heading.Children.Add(_readout);
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        _slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = initial,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _slider.ValueChanged += (_, _) => _readout.Text = $"{(int)Math.Round(_slider.Value)}%";
        Grid.SetRow(_slider, 1);
        root.Children.Add(_slider);

        var hint = new TextBlock
        {
            Text = "100% = volume normal; o multiplicador é aplicado em cima do volume master.",
            FontSize = 10,
            Foreground = new SolidColorBrush(MainWindow.MutedTextColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = MainWindow.BuildFlatButton("Cancelar", MainWindow.NeutralColor, MainWindow.NeutralHoverColor, 100, 32, 12);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = MainWindow.BuildFlatButton("OK", MainWindow.AccentColor, MainWindow.AccentHoverColor, 100, 32, 12);
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.IsDefault = true;
        ok.Click += (_, _) =>
        {
            Result = (int)Math.Round(_slider.Value);
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
    }

    public static int? Pick(Window owner, int initial)
    {
        var dlg = new VolumeDialog(Math.Clamp(initial, 0, 100)) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
