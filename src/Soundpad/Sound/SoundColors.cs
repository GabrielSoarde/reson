namespace Soundpad.Sound;

/// <summary>
/// Curated tile-color palette. New sounds rotate through these (by the current
/// sound count on the board) so a board isn't a single repeated color. Hues
/// chosen for good contrast on the dark board UI. Hex strings (no WPF dep).
/// </summary>
public static class SoundColors
{
    public static readonly string[] Palette =
    {
        "#3b82f6", "#ef4444", "#f59e0b", "#10b981",
        "#8b5cf6", "#ec4899", "#14b8a6", "#f97316",
    };

    /// <summary>Pick a palette color by index, wrapping (and tolerating negatives).</summary>
    public static string PickInitial(int index)
        => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];
}
