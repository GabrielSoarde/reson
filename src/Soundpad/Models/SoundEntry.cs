namespace Soundpad.Models;

public record SoundEntry
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Label { get; init; }
    public string Color { get; init; } = "#3b82f6";
    public string? Icon { get; init; }
    public GridPosition? Position { get; init; }
    // Schema v3: per-sound usage stats. PlayCount is incremented and
    // LastPlayedAt is set to DateTime.UtcNow each time PlaybackEngine
    // accepts a Play for this sound (post-debounce). Always UTC; the
    // frontend localizes for display.
    public int PlayCount { get; init; } = 0;
    public DateTime? LastPlayedAt { get; init; } = null;
}
