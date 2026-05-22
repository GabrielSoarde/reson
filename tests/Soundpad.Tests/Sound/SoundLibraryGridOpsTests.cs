using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryGridOpsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-grid-" + Guid.NewGuid());
    private readonly SoundLibrary _lib;

    public SoundLibraryGridOpsTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "a.mp3"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "b.mp3"), new byte[10]);
        _lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        _lib.Load();
        _lib.AutoScan();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ResizeGrid_Shrinking_Marks_OutOfBounds_As_Null()
    {
        _lib.MutateConfig(c => c with { Grid = new GridLayout(5, 5) });
        var a = _lib.Config.Sounds[0];
        _lib.UpdateSound(a.Id, position: new GridPosition(4, 4));
        _lib.ResizeGrid(2, 2);
        _lib.Config.Sounds.Single(s => s.Id == a.Id).Position.Should().BeNull();
    }

    [Fact]
    public void UpdateSound_Swaps_When_Position_Occupied()
    {
        var ids = _lib.Config.Sounds.Select(s => s.Id).ToList();
        var aPos = _lib.Config.Sounds.Single(s => s.Id == ids[0]).Position!;
        var bPos = _lib.Config.Sounds.Single(s => s.Id == ids[1]).Position!;
        _lib.UpdateSound(ids[0], position: bPos);
        _lib.Config.Sounds.Single(s => s.Id == ids[0]).Position.Should().Be(bPos);
        _lib.Config.Sounds.Single(s => s.Id == ids[1]).Position.Should().Be(aPos);
    }

    [Fact]
    public void UpdateSound_Renames_Label_And_Color()
    {
        var id = _lib.Config.Sounds[0].Id;
        _lib.UpdateSound(id, label: "NEW NAME", color: "#ff0000");
        var updated = _lib.Config.Sounds.Single(s => s.Id == id);
        updated.Label.Should().Be("NEW NAME");
        updated.Color.Should().Be("#ff0000");
    }

    [Fact]
    public void DeleteSound_Removes_Entry()
    {
        var id = _lib.Config.Sounds[0].Id;
        _lib.DeleteSound(id, deleteFile: false);
        _lib.Config.Sounds.Should().NotContain(s => s.Id == id);
    }

    [Fact]
    public void DeleteSound_With_DeleteFile_True_Removes_File()
    {
        var entry = _lib.Config.Sounds[0];
        _lib.DeleteSound(entry.Id, deleteFile: true);
        File.Exists(Path.Combine(_tempDir, "sounds", entry.File)).Should().BeFalse();
    }

    [Fact]
    public void ApplyLayout_Atomic_Rejects_Duplicates()
    {
        var ids = _lib.Config.Sounds.Select(s => s.Id).ToList();
        var dup = new[]
        {
            (ids[0], (GridPosition?)new GridPosition(0, 0)),
            (ids[1], (GridPosition?)new GridPosition(0, 0)),
        };
        Assert.Throws<InvalidOperationException>(() => _lib.ApplyLayout(dup));
    }

    [Fact]
    public void ApplyLayout_Atomic_Applies_All_Or_Nothing()
    {
        var ids = _lib.Config.Sounds.Select(s => s.Id).ToList();
        var placements = new (string, GridPosition?)[]
        {
            (ids[0], new GridPosition(2, 0)),
            (ids[1], new GridPosition(0, 2)),
        };
        _lib.ApplyLayout(placements);
        _lib.Config.Sounds.Single(s => s.Id == ids[0]).Position.Should().Be(new GridPosition(2, 0));
        _lib.Config.Sounds.Single(s => s.Id == ids[1]).Position.Should().Be(new GridPosition(0, 2));
    }
}
