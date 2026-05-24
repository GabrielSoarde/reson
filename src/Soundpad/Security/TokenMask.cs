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
    // Matches the value of a `t` param whether it's in the query (?t=/&t=) or the
    // fragment (#t=). Stops at the next &/# so only the token value is masked.
    private static readonly Regex TParam = new(@"([?&#]t=)([^&#]+)", RegexOptions.Compiled);

    /// <summary>first4…last4, or just "…" when too short to reveal safely.</summary>
    public static string Mask(string token)
    {
        // Need >=12 chars to reveal 4+4 without the two halves overlapping or
        // exposing the whole short token; anything shorter is fully hidden.
        if (string.IsNullOrEmpty(token) || token.Length < 12) return "…";
        return $"{token[..4]}…{token[^4..]}";
    }

    /// <summary>Mask the value of the <c>t</c> query or fragment parameter inside a URL string.</summary>
    public static string MaskUrl(string url) =>
        TParam.Replace(url, m => m.Groups[1].Value + Mask(m.Groups[2].Value));
}
