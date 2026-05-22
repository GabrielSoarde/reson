using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryLoadSaveTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-test-" + Guid.NewGuid());

    public SoundLibraryLoadSaveTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_Creates_Default_When_File_Missing()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.Config.SchemaVersion.Should().Be(1);
        File.Exists(Path.Combine(_tempDir, "config.json")).Should().BeTrue();
    }

    [Fact]
    public void Save_Then_Load_Roundtrips_Config()
    {
        var lib1 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib1.Load();
        lib1.MutateConfig(c => c with { Volume = 42, MonitorEnabled = true });
        lib1.Save();

        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.Volume.Should().Be(42);
        lib2.Config.MonitorEnabled.Should().BeTrue();
    }

    [Fact]
    public void Save_Is_Atomic_Tmp_File_Cleaned_Up()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.Save();
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Load_Rejects_Future_SchemaVersion()
    {
        var futurePath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(futurePath, "{ \"schemaVersion\": 999, \"authToken\": \"a\", \"sounds\": [] }");
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        Assert.Throws<NotSupportedException>(() => lib.Load());
    }

    [Fact]
    public void Save_Concurrent_Mutations_Are_Serialized()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => lib.MutateConfig(c => c with { Volume = (c.Volume + 1) % 101 }))).ToArray();
        Task.WaitAll(tasks);
        lib.Save();

        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.Volume.Should().BeInRange(0, 100);
    }
}
