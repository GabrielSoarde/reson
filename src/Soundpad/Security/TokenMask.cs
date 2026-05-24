using System.Text.RegularExpressions;

namespace Soundpad.Security;

/// <summary>
/// Renders an auth token (or a token-bearing URL) for safe on-screen display:
/// shows only the first and last 4 characters so the user can sanity-check
/// which token it is, without the full secret being readable in a screenshot.
/// The QR code and clipboard still carry the FULL token — this is display-only.
/// </summary>
public static class TokenMask
{
    /// <summary>first4…last4, or just "…" when too short to reveal safely.</summary>
    public static string Mask(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 12) return "…";
        return $"{token[..4]}…{token[^4..]}";
    }

    /// <summary>Mask the value of the <c>t</c> query parameter inside a URL string.</summary>
    public static string MaskUrl(string url)
    {
        return Regex.Replace(url, @"([?&]t=)([^&#]+)", m => m.Groups[1].Value + Mask(m.Groups[2].Value));
    }
}
