namespace Soundpad.Models;

public record SoundEntry
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Label { get; init; }
    public string Color { get; init; } = "#3b82f6";
    public string? Icon { get; init; }
    public GridPosition? Position { get; init; }
}
