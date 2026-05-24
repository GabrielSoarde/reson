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
    // Schema v4: per-sound volume in 0-100 (UI scale). The playback engine
    // multiplies the global volume by (Volume / 100) so a sound effectively
    // ceiling-caps below the master fader. Default 100 = no attenuation,
    // matching pre-v4 behavior so the migration is value-neutral.
    public int Volume { get; init; } = 100;
    // Schema v5: per-sound loudness-normalization gain in dB, computed lazily
    // from the decoded PCM on first play (RMS-based, toward NormalizeTargetDb).
    // null = not yet computed. Applied multiplicatively with Volume + global
    // volume in the engine when SoundConfig.NormalizeEnabled is true.
    public double? NormalizeGainDb { get; init; } = null;
}
