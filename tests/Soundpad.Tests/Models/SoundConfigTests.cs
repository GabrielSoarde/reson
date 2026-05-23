using FluentAssertions;
using Soundpad.Models;

namespace Soundpad.Tests.Models;

public class SoundConfigTests
{
    [Fact]
    public void Default_Has_Current_SchemaVersion()
    {
        // Default is born under the current schema (v3: device fields hold
        // WASAPI endpoint ids, sounds carry PlayCount + LastPlayedAt stats).
        // The v1 → v2 and v2 → v3 migration paths are covered separately by
        // SoundLibraryMigrationTests / SoundLibraryUsageStatsTests.
        var c = SoundConfig.Default();
        c.SchemaVersion.Should().Be(3);
        c.MonitorDevice.Should().BeNull();
        c.MonitorEnabled.Should().BeFalse();
        c.Volume.Should().Be(80);
        c.LatencyMs.Should().Be(50);
        c.Port.Should().Be(8080);
        c.Grid.Cols.Should().Be(3);
        c.Grid.Rows.Should().Be(4);
        c.Sounds.Should().BeEmpty();
        c.AuthToken.Should().HaveLength(32);
    }

    [Fact]
    public void Default_AuthToken_Is_Hex_32Chars()
    {
        var c = SoundConfig.Default();
        c.AuthToken.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
