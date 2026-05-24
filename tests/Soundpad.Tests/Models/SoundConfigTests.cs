using FluentAssertions;
using Soundpad.Models;

namespace Soundpad.Tests.Models;

public class SoundConfigTests
{
    [Fact]
    public void Default_Has_Current_SchemaVersion()
    {
        // Default is born under the current schema (v5: multi-board + normalization, with
        // device fields holding WASAPI endpoint ids and SoundEntry carrying
        // PlayCount + LastPlayedAt + Volume). The v1/v2/v3/v4 → v5 migration
        // paths are covered separately by SoundLibraryMigrationTests.
        var c = SoundConfig.Default();
        c.SchemaVersion.Should().Be(5);
        c.MonitorDevice.Should().BeNull();
        c.MonitorEnabled.Should().BeFalse();
        c.NormalizeEnabled.Should().BeTrue();
        c.Volume.Should().Be(80);
        c.LatencyMs.Should().Be(50);
        c.Port.Should().Be(8080);
        c.AuthToken.Should().HaveLength(32);

        // Multi-board: one default board called "Padrão" with a 3x4 grid,
        // and ActiveBoardId pointed at it.
        c.Boards.Should().HaveCount(1);
        c.ActiveBoardId.Should().Be(c.Boards[0].Id);
        c.Boards[0].Name.Should().Be("Padrão");
        c.Boards[0].Grid.Cols.Should().Be(3);
        c.Boards[0].Grid.Rows.Should().Be(4);
        c.Boards[0].Sounds.Should().BeEmpty();
    }

    [Fact]
    public void Default_AuthToken_Is_Hex_32Chars()
    {
        var c = SoundConfig.Default();
        c.AuthToken.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
