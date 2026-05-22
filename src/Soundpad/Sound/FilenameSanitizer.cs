using System.Text;
using System.Text.RegularExpressions;

namespace Soundpad.Sound;

public static class FilenameSanitizer
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Regex AllowedChars = new(@"[A-Za-z0-9 _.()\[\]\-]", RegexOptions.Compiled);

    // Matches a leading path-traversal sequence like "..\" or "../" so we can
    // collapse it to a single underscore (defang) instead of just neutering the
    // separator and leaving the dots intact.
    private static readonly Regex LeadingTraversal = new(@"^\.\.[\\/]", RegexOptions.Compiled);

    public static string Sanitize(string input, string fallbackId, string extension = "")
    {
        // Collapse a leading "..\" or "../" to "__" before per-char sanitization
        // so "..\evil.mp3" becomes "__evil.mp3" (the leading dots get defanged
        // alongside the separator) rather than ".._evil.mp3" (where the dots
        // would survive because "." is otherwise an allowed character).
        var preprocessed = LeadingTraversal.Replace(input, "__");

        var sb = new StringBuilder(preprocessed.Length);
        foreach (var ch in preprocessed)
        {
            if (ch < ' ' || ch == 0x7F) { sb.Append('_'); continue; }
            sb.Append(AllowedChars.IsMatch(ch.ToString()) ? ch : '_');
        }

        var trimmed = sb.ToString().TrimEnd(' ', '.');

        // Fall back when the stem (name without extension) is empty — covers
        // both empty/whitespace input and dotfile-style ".mp3" where nothing
        // remains before the extension.
        var stem = Path.GetFileNameWithoutExtension(trimmed);
        if (string.IsNullOrEmpty(stem))
            return string.IsNullOrEmpty(extension) ? fallbackId : fallbackId + extension;

        if (ReservedNames.Contains(stem))
            return "_" + trimmed;

        return trimmed;
    }
}
