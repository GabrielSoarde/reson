using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

/// <summary>
/// Covers schema v3 per-sound usage stats (PlayCount + LastPlayedAt) and the
/// SoundLibrary.RecordPlay surface that PlaybackEngine.HandlePlay calls.
/// </summary>
public class SoundLibraryUsageStatsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-stats-" + Guid.NewGuid());

    public SoundLibraryUsageStatsTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "a.mp3"), new byte[10]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private SoundLibrary BuildLibraryWithOneSound()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        lib.Config.Sounds.Should().HaveCount(1);
        return lib;
    }

    [Fact]
    public void RecordPlay_Increments_Count_And_Sets_Timestamp()
    {
        var lib = BuildLibraryWithOneSound();
        var id = lib.Config.Sounds[0].Id;
        var before = DateTime.UtcNow.AddSeconds(-1); // tolerance for clock skew

        lib.RecordPlay(id);
        lib.RecordPlay(id);
        lib.RecordPlay(id);

        var entry = lib.Config.Sounds[0];
        entry.PlayCount.Should().Be(3);
        entry.LastPlayedAt.Should().NotBeNull();
        entry.LastPlayedAt!.Value.Should().BeAfter(before);
        entry.LastPlayedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void RecordPlay_Unknown_Id_Is_NoOp()
    {
        var lib = BuildLibraryWithOneSound();
        var beforeCount = lib.Config.Sounds[0].PlayCount;
        var beforeStamp = lib.Config.Sounds[0].LastPlayedAt;

        // Phantom id (mid-delete race) — must not throw.
        var act = () => lib.RecordPlay("does-not-exist");
        act.Should().NotThrow();

        lib.Config.Sounds[0].PlayCount.Should().Be(beforeCount);
        lib.Config.Sounds[0].LastPlayedAt.Should().Be(beforeStamp);
    }

    [Fact]
    public void RecordPlay_Persists_To_Disk()
    {
        var lib = BuildLibraryWithOneSound();
        var id = lib.Config.Sounds[0].Id;
        lib.RecordPlay(id);
        lib.RecordPlay(id);

        // Reload from the same dir — values must round-trip.
        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        var reloaded = lib2.Config.Sounds.Single(s => s.Id == id);
        reloaded.PlayCount.Should().Be(2);
        reloaded.LastPlayedAt.Should().NotBeNull();
    }

    [Fact]
    public void RecordPlay_Raises_Changed_Event()
    {
        var lib = BuildLibraryWithOneSound();
        var id = lib.Config.Sounds[0].Id;
        var fired = 0;
        lib.Changed += () => Interlocked.Increment(ref fired);

        lib.RecordPlay(id);

        fired.Should().Be(1);
    }

    [Fact]
    public void Schema_V2_Loads_With_Zero_Counts()
    {
        // Hand-write a v2 config with one sound entry (no PlayCount /
        // LastPlayedAt fields). Load must accept it, default the new fields
        // to 0 / null, and auto-bump SchemaVersion to 3.
        var v2Json = """
        {
          "schemaVersion": 2,
          "authToken": "abc",
          "port": 8080,
          "monitorEnabled": false,
          "volume": 80,
          "latencyMs": 50,
          "grid": { "cols": 3, "rows": 4 },
          "sounds": [
            {
              "id": "a",
              "file": "a.mp3",
              "label": "A",
              "color": "#3b82f6"
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), v2Json);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        lib.Config.SchemaVersion.Should().Be(3);
        var entry = lib.Config.Sounds.Single();
        entry.PlayCount.Should().Be(0);
        entry.LastPlayedAt.Should().BeNull();
    }

    [Fact]
    public void Schema_V3_Round_Trips()
    {
        // Write a v3 config with non-zero stats — load must preserve them.
        var stamp = new DateTime(2026, 5, 22, 14, 30, 0, DateTimeKind.Utc);
        var v3Json = $$"""
        {
          "schemaVersion": 3,
          "authToken": "abc",
          "port": 8080,
          "monitorEnabled": false,
          "volume": 80,
          "latencyMs": 50,
          "grid": { "cols": 3, "rows": 4 },
          "sounds": [
            {
              "id": "a",
              "file": "a.mp3",
              "label": "A",
              "color": "#3b82f6",
              "playCount": 42,
              "lastPlayedAt": "{{stamp:yyyy-MM-ddTHH:mm:ssZ}}"
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), v3Json);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        var entry = lib.Config.Sounds.Single();
        entry.PlayCount.Should().Be(42);
        entry.LastPlayedAt.Should().NotBeNull();
        entry.LastPlayedAt!.Value.ToUniversalTime().Should().Be(stamp);
    }
}
