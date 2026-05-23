using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace Soundpad.Tray;

public class QrWindow : Form
{
    public QrWindow(string url)
    {
        Text = "Reson — QR";
        Size = new Size(380, 420);
        StartPosition = FormStartPosition.CenterScreen;
        TrySetFormIcon();

        using var g = new QRCodeGenerator();
        using var data = g.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(20);
        using var ms = new MemoryStream(bytes);
        var img = Image.FromStream(ms);

        var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
        var lbl = new Label { Text = url, Dock = DockStyle.Bottom, Height = 32, TextAlign = ContentAlignment.MiddleCenter };
        Controls.Add(pb);
        Controls.Add(lbl);
    }

    /// <summary>
    /// Pull Reson.ico from the embedded WPF resource manifest. The pack scheme
    /// requires a WPF Application to be initialized first; under our runtime
    /// model (WpfHost starts the Application before any QrWindow opens) this is
    /// always true. Best-effort: a missing icon falls back to the default WinForms
    /// title-bar glyph.
    /// </summary>
    private void TrySetFormIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Reson.ico", UriKind.Absolute));
            if (res is null) return;
            using var s = res.Stream;
            Icon = new Icon(s);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"QrWindow.TrySetFormIcon: {ex.Message}");
        }
    }
}
