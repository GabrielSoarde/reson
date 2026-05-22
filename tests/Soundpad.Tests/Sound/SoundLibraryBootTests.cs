using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryBootTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-boot-" + Guid.NewGuid());

    public SoundLibraryBootTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void AutoScan_Adds_New_Files_From_Sounds_Folder()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "drop.mp3"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "boom.wav"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        lib.Config.Sounds.Should().HaveCount(2);
        lib.Config.Sounds.Should().Contain(s => s.File == "drop.mp3");
        lib.Config.Sounds.Should().Contain(s => s.File == "boom.wav");
    }

    [Fact]
    public void AutoScan_Skips_Files_Already_In_Config()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "x.mp3"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        lib.AutoScan(); // second call should not double-add
        lib.Config.Sounds.Should().HaveCount(1);
    }

    [Fact]
    public void RuntimeStatus_Marks_Missing_When_File_Deleted()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "x.mp3"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        File.Delete(Path.Combine(_tempDir, "sounds", "x.mp3"));
        var status = lib.GetRuntimeStatuses();
        status.Should().ContainSingle(s => s.Id == lib.Config.Sounds[0].Id && s.Missing);
    }

    [Fact]
    public void RuntimeStatus_Not_Persisted_To_Config()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "x.mp3"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        File.Delete(Path.Combine(_tempDir, "sounds", "x.mp3"));
        lib.GetRuntimeStatuses();
        lib.Save();
        File.ReadAllText(Path.Combine(_tempDir, "config.json"))
            .Should().NotContain("missing");
    }

    [Fact]
    public void Repair_Fixes_Duplicate_Positions_On_Load()
    {
        var bad = @"{
            ""schemaVersion"": 1,
            ""authToken"": ""abc"",
            ""grid"": { ""cols"": 3, ""rows"": 3 },
            ""sounds"": [
                { ""id"": ""a"", ""file"": ""a.mp3"", ""label"": ""A"", ""position"": { ""col"": 0, ""row"": 0 } },
                { ""id"": ""b"", ""file"": ""b.mp3"", ""label"": ""B"", ""position"": { ""col"": 0, ""row"": 0 } }
            ]
        }";
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), bad);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.RepairInvariants();
        lib.Config.Sounds[0].Position.Should().Be(new GridPosition(0, 0));
        lib.Config.Sounds[1].Position.Should().BeNull();
    }
}
