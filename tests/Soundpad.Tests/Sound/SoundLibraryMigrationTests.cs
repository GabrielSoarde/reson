using FluentAssertions;
using Moq;
using Soundpad.Audio;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

/// <summary>
/// Covers the v1 → v2 device-identifier migration. Schema v1 stored
/// FriendlyNames in <c>audioDevice</c>/<c>monitorDevice</c>/<c>micDevice</c>;
/// v2 stores stable WASAPI endpoint ids. The migration runs once on first
/// launch after upgrade, translates names → ids where possible (drops the
/// field to null when the device is gone), and persists schemaVersion=2.
/// </summary>
public class SoundLibraryMigrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-migr-" + Guid.NewGuid());

    public SoundLibraryMigrationTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static DeviceLocator BuildLocator(
        (string Id, string Name)[]? render = null,
        (string Id, string Name)[]? capture = null)
    {
        var m = new Mock<IAudioDeviceEnumerator>();
        m.Setup(e => e.EnumerateRenderDevices()).Returns(
            (render ?? Array.Empty<(string, string)>())
                .Select(t => new AudioDeviceInfo(t.Id, t.Name)).ToList());
        m.Setup(e => e.EnumerateCaptureDevices()).Returns(
            (capture ?? Array.Empty<(string, string)>())
                .Select(t => new AudioDeviceInfo(t.Id, t.Name)).ToList());
        return new DeviceLocator(m.Object);
    }

    private void WriteV1Config(string? audioDevice, string? monitorDevice, string? micDevice)
    {
        var json = $$"""
        {
          "schemaVersion": 1,
          "authToken": "abc",
          "port": 8080,
          "audioDevice": {{ToJsonString(audioDevice)}},
          "monitorDevice": {{ToJsonString(monitorDevice)}},
          "micDevice": {{ToJsonString(micDevice)}},
          "monitorEnabled": false,
          "volume": 80,
          "latencyMs": 50,
          "grid": { "cols": 3, "rows": 4 },
          "sounds": []
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), json);
    }

    private static string ToJsonString(string? s) => s is null ? "null" : $"\"{s}\"";

    [Fact]
    public void Migration_Translates_FriendlyNames_To_Ids_And_Bumps_Schema()
    {
        WriteV1Config(
            audioDevice: "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)",
            monitorDevice: "Headphones (Realtek)",
            micDevice: "Logitech G935");
        var loc = BuildLocator(
            render: new[] {
                ("{vm-id}", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"),
                ("{hp-id}", "Headphones (Realtek)"),
            },
            capture: new[] {
                ("{mic-id}", "Logitech G935"),
            });

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.Config.SchemaVersion.Should().Be(1);

        lib.MigrateDeviceIdentifiers(loc);

        lib.Config.SchemaVersion.Should().Be(2);
        lib.Config.AudioDevice.Should().Be("{vm-id}");
        lib.Config.MonitorDevice.Should().Be("{hp-id}");
        lib.Config.MicDevice.Should().Be("{mic-id}");
    }

    [Fact]
    public void Migration_Drops_Missing_Devices_To_Null()
    {
        WriteV1Config(
            audioDevice: "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)",
            monitorDevice: "Some Device That Was Uninstalled",
            micDevice: null);
        var loc = BuildLocator(
            render: new[] { ("{vm-id}", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)") });

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.MigrateDeviceIdentifiers(loc);

        lib.Config.SchemaVersion.Should().Be(2);
        lib.Config.AudioDevice.Should().Be("{vm-id}");
        lib.Config.MonitorDevice.Should().BeNull(); // unresolvable → cleared
        lib.Config.MicDevice.Should().BeNull();
    }

    [Fact]
    public void Migration_Is_Idempotent_On_V2_Configs()
    {
        // First migrate v1 → v2
        WriteV1Config(audioDevice: "Speakers", monitorDevice: null, micDevice: null);
        var loc = BuildLocator(render: new[] { ("{spk-id}", "Speakers") });
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.MigrateDeviceIdentifiers(loc);
        lib.Config.SchemaVersion.Should().Be(2);
        var firstId = lib.Config.AudioDevice;

        // Reload and call migrate again — should be a no-op
        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.MigrateDeviceIdentifiers(loc);
        lib2.Config.SchemaVersion.Should().Be(2);
        lib2.Config.AudioDevice.Should().Be(firstId);
    }

    [Fact]
    public void Migration_Persists_Updated_Config_To_Disk()
    {
        WriteV1Config(audioDevice: "Speakers", monitorDevice: null, micDevice: null);
        var loc = BuildLocator(render: new[] { ("{spk-id}", "Speakers") });
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.MigrateDeviceIdentifiers(loc);

        // Read the file directly to confirm it was rewritten
        var raw = File.ReadAllText(Path.Combine(_tempDir, "config.json"));
        raw.Should().Contain("\"schemaVersion\": 2");
        raw.Should().Contain("{spk-id}");
    }

    [Fact]
    public void Migration_Preserves_AlreadyId_Values_Defensively()
    {
        // Edge case: a v1 config that somehow already contains an id-shaped
        // string (e.g. a user edited config.json manually). The migration
        // detects the id is already valid and keeps it as-is.
        var existingId = "{0.0.0.00000000}.{existing-id}";
        WriteV1Config(audioDevice: existingId, monitorDevice: null, micDevice: null);
        var loc = BuildLocator(render: new[] { (existingId, "Speakers (renamed)") });
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.MigrateDeviceIdentifiers(loc);

        lib.Config.AudioDevice.Should().Be(existingId);
        lib.Config.SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void Migration_NoOp_On_V2_Schema_Returns_Without_Mutation()
    {
        // Write a config that's already v2 directly
        var v2Json = """
        {
          "schemaVersion": 2,
          "authToken": "abc",
          "port": 8080,
          "audioDevice": "{0.0.0.00000000}.{existing}",
          "monitorEnabled": false,
          "volume": 80,
          "latencyMs": 50,
          "grid": { "cols": 3, "rows": 4 },
          "sounds": []
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), v2Json);
        var loc = BuildLocator(); // empty enumerator
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.Config.SchemaVersion.Should().Be(2);

        lib.MigrateDeviceIdentifiers(loc);

        // Still v2, AudioDevice preserved (not nulled, even though the
        // enumerator can't see it — we never re-validate v2 ids on migration)
        lib.Config.SchemaVersion.Should().Be(2);
        lib.Config.AudioDevice.Should().Be("{0.0.0.00000000}.{existing}");
    }
}
