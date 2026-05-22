using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class GridPositionerTests
{
    private static SoundEntry Entry(string id, int? col = null, int? row = null)
        => new() {
            Id = id, File = id + ".mp3", Label = id,
            Position = col.HasValue && row.HasValue ? new GridPosition(col.Value, row.Value) : null
        };

    [Fact]
    public void NextFree_Empty_Grid_Returns_0_0()
    {
        var grid = new GridLayout(3, 4);
        GridPositioner.NextFree(grid, new List<SoundEntry>())
            .Should().Be(new GridPosition(0, 0));
    }

    [Fact]
    public void NextFree_Returns_RowMajor_Order()
    {
        var grid = new GridLayout(3, 4);
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 1, 0) };
        GridPositioner.NextFree(grid, sounds).Should().Be(new GridPosition(2, 0));
    }

    [Fact]
    public void NextFree_Skips_Occupied_Cells()
    {
        var grid = new GridLayout(3, 2);
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 2, 0) };
        GridPositioner.NextFree(grid, sounds).Should().Be(new GridPosition(1, 0));
    }

    [Fact]
    public void NextFree_Returns_Null_When_Full()
    {
        var grid = new GridLayout(2, 1);
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 1, 0) };
        GridPositioner.NextFree(grid, sounds).Should().BeNull();
    }

    [Fact]
    public void Repair_Marks_Duplicates_As_Null_First_Wins()
    {
        var grid = new GridLayout(3, 3);
        var sounds = new List<SoundEntry>
        {
            Entry("a", 0, 0), Entry("b", 0, 0), Entry("c", 1, 1)
        };
        var repaired = GridPositioner.RepairDuplicates(grid, sounds);
        repaired[0].Position.Should().Be(new GridPosition(0, 0));
        repaired[1].Position.Should().BeNull();
        repaired[2].Position.Should().Be(new GridPosition(1, 1));
    }

    [Fact]
    public void Repair_Marks_OutOfBounds_As_Null()
    {
        var grid = new GridLayout(2, 2);
        var sounds = new List<SoundEntry> { Entry("a", 5, 5), Entry("b", 0, 0) };
        var repaired = GridPositioner.RepairDuplicates(grid, sounds);
        repaired[0].Position.Should().BeNull();
        repaired[1].Position.Should().Be(new GridPosition(0, 0));
    }

    [Fact]
    public void Swap_Exchanges_Positions()
    {
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 1, 1) };
        var result = GridPositioner.Swap(sounds, "a", new GridPosition(1, 1));
        result.Single(s => s.Id == "a").Position.Should().Be(new GridPosition(1, 1));
        result.Single(s => s.Id == "b").Position.Should().Be(new GridPosition(0, 0));
    }

    [Fact]
    public void Swap_To_Empty_Cell_Just_Moves()
    {
        var sounds = new List<SoundEntry> { Entry("a", 0, 0) };
        var result = GridPositioner.Swap(sounds, "a", new GridPosition(2, 2));
        result.Single().Position.Should().Be(new GridPosition(2, 2));
    }
}
