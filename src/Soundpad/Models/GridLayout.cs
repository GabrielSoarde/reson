namespace Soundpad.Models;

public record GridLayout(int Cols, int Rows)
{
    public bool Contains(GridPosition p) =>
        p.Col >= 0 && p.Col < Cols && p.Row >= 0 && p.Row < Rows;

    public int CellCount => Cols * Rows;
}
