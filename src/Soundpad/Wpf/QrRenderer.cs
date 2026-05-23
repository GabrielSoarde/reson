using System.Windows.Media.Imaging;
using QRCoder;

namespace Soundpad.Wpf;

/// <summary>
/// Pair of QR-rendering helpers shared by the tray's <see cref="Tray.QrWindow"/>
/// (WinForms / System.Drawing.Image) and the WPF sidebar in <see cref="MainWindow"/>
/// (BitmapImage). Both surfaces want the same PNG bytes — the only difference
/// is the final image type they need to feed their respective UI frameworks.
///
/// <para>Threading: pure compute, safe from any thread.</para>
/// </summary>
internal static class QrRenderer
{
    /// <summary>
    /// Generate a PNG-encoded QR code for <paramref name="payload"/>. The
    /// pixel-per-module size of 20 matches the legacy QrWindow rendering so
    /// the modal still looks identical after the refactor.
    /// </summary>
    public static byte[] RenderPng(string payload, int pixelsPerModule = 20)
    {
        using var g = new QRCodeGenerator();
        using var data = g.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Render <paramref name="payload"/> as a frozen <see cref="BitmapImage"/>
    /// ready to bind to a WPF <c>Image.Source</c>. Frozen so it can cross
    /// threads safely (background-rendered then assigned on the Dispatcher).
    ///
    /// Decode pixel width is hinted via <c>DecodePixelWidth</c> so a 1024px QR
    /// doesn't blow VRAM on a 280-px sidebar.
    /// </summary>
    public static BitmapImage RenderBitmap(string payload, int pixelsPerModule = 10, int decodePixelWidth = 320)
    {
        var bytes = RenderPng(payload, pixelsPerModule);
        var img = new BitmapImage();
        img.BeginInit();
        // OnLoad caches the decoded pixels and releases the stream — required
        // because we close the MemoryStream as soon as EndInit returns.
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = decodePixelWidth;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }
}
