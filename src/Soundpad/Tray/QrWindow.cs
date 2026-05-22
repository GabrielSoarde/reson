using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace Soundpad.Tray;

public class QrWindow : Form
{
    public QrWindow(string url)
    {
        Text = "Soundpad — QR";
        Size = new Size(380, 420);
        StartPosition = FormStartPosition.CenterScreen;

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
}
