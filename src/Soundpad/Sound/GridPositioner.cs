using Soundpad.Models;

namespace Soundpad.Sound;

public static class GridPositioner
{
    public static GridPosition? NextFree(GridLayout grid, IReadOnlyList<SoundEntry> sounds)
    {
        var occupied = new HashSet<GridPosition>(
            sounds.Where(s => s.Position is not null).Select(s => s.Position!));
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
        {
            var p = new GridPosition(col, row);
            if (!occupied.Contains(p)) return p;
        }
        return null;
    }

    public static List<SoundEntry> RepairDuplicates(GridLayout grid, IReadOnlyList<SoundEntry> sounds)
    {
        var seen = new HashSet<GridPosition>();
        var result = new List<SoundEntry>(sounds.Count);
        foreach (var s in sounds)
        {
            if (s.Position is null) { result.Add(s); continue; }
            if (!grid.Contains(s.Position) || !seen.Add(s.Position))
                result.Add(s with { Position = null });
            else
                result.Add(s);
        }
        return result;
    }

    public static List<SoundEntry> Swap(IReadOnlyList<SoundEntry> sounds, string movingId, GridPosition target)
    {
        var moving = sounds.Single(s => s.Id == movingId);
        var occupant = sounds.FirstOrDefault(s => s.Id != movingId && s.Position == target);
        var result = new List<SoundEntry>(sounds.Count);
        foreach (var s in sounds)
        {
            if (s.Id == movingId) result.Add(s with { Position = target });
            else if (occupant is not null && s.Id == occupant.Id) result.Add(s with { Position = moving.Position });
            else result.Add(s);
        }
        return result;
    }
}
