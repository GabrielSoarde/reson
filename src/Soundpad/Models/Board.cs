namespace Soundpad.Models;

/// <summary>
/// A named soundboard: its own grid layout, palette pill color, and list of
/// sounds. Multi-board support landed in schema v4 — schema v3 had a single
/// implicit board pinned at the top level of <see cref="SoundConfig"/>.
///
/// <para>Sound ownership model: each board owns its own sounds. The same
/// underlying audio file CAN appear under two boards (each gets its own
/// <see cref="SoundEntry"/> with an independent id, position, color, volume),
/// but moving a sound between boards is an explicit re-parenting operation
/// (see <see cref="Sound.SoundLibrary"/>.MoveSoundToBoard). This matches
/// Deckboard's "decks are independent" UX — users expect to organize sounds
/// by context (game/stream/work) without surprising cross-board edits.</para>
/// </summary>
public record Board
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Accent color for the boards-panel pill (hex). Defaults to the
    /// Reson accent blue if the caller doesn't set it.</summary>
    public string Color { get; init; } = "#3b82f6";
    public GridLayout Grid { get; init; } = new(3, 4);
    public List<SoundEntry> Sounds { get; init; } = new();
}
