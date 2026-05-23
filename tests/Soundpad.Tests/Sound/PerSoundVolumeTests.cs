using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

/// <summary>
/// Schema v4 per-sound volume — covers the library-level CRUD (UpdateSound
/// volume parameter, persistence, clamp at 0/100). Engine-level effective
/// volume math is covered by <see cref="Audio.PerSoundEngineVolumeTests"/>.
/// </summary>
public class PerSoundVolumeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-volperound-" + Guid.NewGuid());
    private readonly SoundLibrary _lib;

    public PerSoundVolumeTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "a.mp3"), new byte[10]);
        _lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        _lib.Load();
        _lib.AutoScan();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Default_Sound_Volume_Is_100()
    {
        _lib.ActiveBoard.Sounds.Single().Volume.Should().Be(100);
    }

    [Fact]
    public void UpdateSound_Sets_Volume()
    {
        var id = _lib.ActiveBoard.Sounds.Single().Id;
        _lib.UpdateSound(id, volume: 42);
        _lib.ActiveBoard.Sounds.Single().Volume.Should().Be(42);
    }

    [Fact]
    public void UpdateSound_Clamps_Volume_To_0_100_Range()
    {
        var id = _lib.ActiveBoard.Sounds.Single().Id;
        _lib.UpdateSound(id, volume: 250);
        _lib.ActiveBoard.Sounds.Single().Volume.Should().Be(100);
        _lib.UpdateSound(id, volume: -5);
        _lib.ActiveBoard.Sounds.Single().Volume.Should().Be(0);
    }

    [Fact]
    public void UpdateSound_Null_Volume_Preserves_Existing()
    {
        var id = _lib.ActiveBoard.Sounds.Single().Id;
        _lib.UpdateSound(id, volume: 55);
        // Subsequent update without volume= keeps the 55.
        _lib.UpdateSound(id, label: "New label");
        _lib.ActiveBoard.Sounds.Single().Volume.Should().Be(55);
        _lib.ActiveBoard.Sounds.Single().Label.Should().Be("New label");
    }

    [Fact]
    public void Volume_Persists_To_Disk_And_Round_Trips()
    {
        var id = _lib.ActiveBoard.Sounds.Single().Id;
        _lib.UpdateSound(id, volume: 33);

        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.ActiveBoard.Sounds.Single(s => s.Id == id).Volume.Should().Be(33);
    }
}
