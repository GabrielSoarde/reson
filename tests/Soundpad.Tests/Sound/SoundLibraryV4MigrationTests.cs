using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

/// <summary>
/// Covers the v3 → v4 structural migration: lift the top-level Grid + Sounds
/// in a single "default" board called "Padrão" and add Boards + ActiveBoardId.
/// Also exercises the v1 → v4 and v2 → v4 paths (older configs upgrade
/// through to current on first load).
/// </summary>
public class SoundLibraryV4MigrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-v4-" + Guid.NewGuid());

    public SoundLibraryV4MigrationTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_tempDir, "config.json");

    [Fact]
    public void V3_To_V4_Wraps_Top_Level_Grid_And_Sounds_In_Default_Board()
    {
        // Write a typical v3 config with a populated grid and two sounds.
        var v3Json = """
        {
          "schemaVersion": 3,
          "authToken": "abc",
          "port": 8080,
          "monitorEnabled": false,
          "volume": 70,
          "latencyMs": 50,
          "grid": { "cols": 4, "rows": 5 },
          "sounds": [
            { "id": "alpha", "file": "alpha.mp3", "label": "Alpha", "color": "#22c55e",
              "position": { "col": 0, "row": 0 }, "playCount": 3, "lastPlayedAt": "2026-05-22T10:00:00Z" },
            { "id": "beta", "file": "beta.mp3", "label": "Beta", "color": "#ef4444",
              "position": { "col": 1, "row": 0 } }
          ]
        }
        """;
        File.WriteAllText(ConfigPath, v3Json);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        lib.Config.SchemaVersion.Should().Be(5);
        lib.Config.Boards.Should().HaveCount(1);
        lib.Config.ActiveBoardId.Should().Be("default");

        var board = lib.Config.Boards[0];
        board.Id.Should().Be("default");
        board.Name.Should().Be("Padrão");
        board.Grid.Cols.Should().Be(4);
        board.Grid.Rows.Should().Be(5);
        board.Sounds.Should().HaveCount(2);
        board.Sounds.Should().Contain(s => s.Id == "alpha" && s.Color == "#22c55e" && s.PlayCount == 3);
        board.Sounds.Should().Contain(s => s.Id == "beta" && s.Color == "#ef4444");
    }

    [Fact]
    public void V3_To_V4_Preserves_Top_Level_Device_And_Auth_Fields()
    {
        var v3Json = """
        {
          "schemaVersion": 3,
          "authToken": "deadbeefcafebabe",
          "port": 9090,
          "audioDevice": "{0.0.0.x}.{game}",
          "monitorDevice": "{0.0.0.x}.{monitor}",
          "micDevice": "{0.0.0.x}.{mic}",
          "monitorEnabled": true,
          "volume": 65,
          "latencyMs": 40,
          "grid": { "cols": 3, "rows": 4 },
          "sounds": []
        }
        """;
        File.WriteAllText(ConfigPath, v3Json);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        lib.Config.AuthToken.Should().Be("deadbeefcafebabe");
        lib.Config.Port.Should().Be(9090);
        lib.Config.AudioDevice.Should().Be("{0.0.0.x}.{game}");
        lib.Config.MonitorDevice.Should().Be("{0.0.0.x}.{monitor}");
        lib.Config.MicDevice.Should().Be("{0.0.0.x}.{mic}");
        lib.Config.MonitorEnabled.Should().BeTrue();
        lib.Config.Volume.Should().Be(65);
        lib.Config.LatencyMs.Should().Be(40);
    }

    [Fact]
    public void V3_To_V4_Volume_Field_Defaults_To_100()
    {
        // v3 sounds had no Volume field — the v4 migration plus record
        // initializer defaults should give them Volume = 100.
        var v3Json = """
        {
          "schemaVersion": 3,
          "authToken": "abc",
          "grid": { "cols": 3, "rows": 4 },
          "sounds": [
            { "id": "a", "file": "a.mp3", "label": "A" }
          ]
        }
        """;
        File.WriteAllText(ConfigPath, v3Json);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.ActiveBoard.Sounds.Single().Volume.Should().Be(100);
    }

    [Fact]
    public void V3_To_V4_Persists_To_Disk_So_Migration_Is_OneShot()
    {
        var v3Json = """
        {
          "schemaVersion": 3,
          "authToken": "abc",
          "grid": { "cols": 3, "rows": 4 },
          "sounds": [ { "id": "a", "file": "a.mp3", "label": "A" } ]
        }
        """;
        File.WriteAllText(ConfigPath, v3Json);

        // First load triggers migration + save
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        // Re-read the file directly to confirm the v5 shape was written.
        var raw = File.ReadAllText(ConfigPath);
        raw.Should().Contain("\"schemaVersion\": 5");
        raw.Should().Contain("\"boards\":");
        raw.Should().Contain("\"activeBoardId\":");
        raw.Should().NotContain("\"grid\": {\n  ");  // grid no longer at top level

        // Second load doesn't double-wrap — boards list still has one entry.
        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.Boards.Should().HaveCount(1);
        lib2.Config.Boards[0].Sounds.Should().HaveCount(1);
    }

    [Fact]
    public void V1_To_V4_Walks_All_Intermediate_Schemas()
    {
        // Schema v1 = FriendlyName device fields + top-level grid/sounds.
        // Load() does the structural v3 → v4 wrap; the FriendlyNames stay
        // verbatim until the user calls MigrateDeviceIdentifiers (this test
        // doesn't bother with that — we just want to confirm the structural
        // bump works even from the oldest known shape).
        var v1Json = """
        {
          "schemaVersion": 1,
          "authToken": "abc",
          "audioDevice": "VoiceMeeter Input (VB-Audio)",
          "grid": { "cols": 3, "rows": 4 },
          "sounds": [ { "id": "old", "file": "old.mp3", "label": "Old" } ]
        }
        """;
        File.WriteAllText(ConfigPath, v1Json);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        lib.Config.SchemaVersion.Should().Be(5);
        lib.Config.Boards.Should().HaveCount(1);
        lib.ActiveBoard.Sounds.Should().ContainSingle(s => s.Id == "old");
        // FriendlyName preserved verbatim — MigrateDeviceIdentifiers is the
        // explicit step that translates it to an endpoint id.
        lib.Config.AudioDevice.Should().Be("VoiceMeeter Input (VB-Audio)");
    }

    [Fact]
    public void Empty_V3_Config_Migrates_To_V4_With_Empty_Default_Board()
    {
        var v3Json = """
        {
          "schemaVersion": 3,
          "authToken": "abc",
          "grid": { "cols": 3, "rows": 4 },
          "sounds": []
        }
        """;
        File.WriteAllText(ConfigPath, v3Json);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        lib.Config.Boards.Should().HaveCount(1);
        lib.Config.Boards[0].Sounds.Should().BeEmpty();
        lib.Config.Boards[0].Grid.Cols.Should().Be(3);
    }
}
