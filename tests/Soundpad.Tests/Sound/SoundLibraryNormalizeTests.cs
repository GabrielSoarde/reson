using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryNormalizeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "reson-norm-" + Guid.NewGuid());
    private readonly SoundLibrary _lib;

    public SoundLibraryNormalizeTests()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "a.mp3"), new byte[10]);
        _lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        _lib.Load();
        _lib.Upload("a.mp3", new MemoryStream(new byte[10])); // one entry on active board
    }
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public void SetNormalizeGainDb_Persists_And_Raises_Changed()
    {
        var id = _lib.ActiveBoard.Sounds[0].Id;
        var raised = 0; _lib.Changed += () => Interlocked.Increment(ref raised);
        _lib.SetNormalizeGainDb(id, -3.5);
        _lib.ActiveBoard.Sounds[0].NormalizeGainDb.Should().Be(-3.5);
        raised.Should().BeGreaterThan(0);

        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.ActiveBoard.Sounds[0].NormalizeGainDb.Should().Be(-3.5);
    }

    [Fact]
    public void SetNormalizeGainDb_Unknown_Id_Is_NoOp()
    {
        _lib.Invoking(l => l.SetNormalizeGainDb("nope", -3.0)).Should().NotThrow();
    }

    [Fact]
    public void SetNormalizeEnabled_Persists()
    {
        _lib.SetNormalizeEnabled(false);
        _lib.Config.NormalizeEnabled.Should().BeFalse();
        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.NormalizeEnabled.Should().BeFalse();
    }
}
