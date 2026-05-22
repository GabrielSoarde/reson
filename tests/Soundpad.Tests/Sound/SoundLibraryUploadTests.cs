using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryUploadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-up-" + Guid.NewGuid());
    private readonly SoundLibrary _lib;

    public SoundLibraryUploadTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        _lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        _lib.Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static MemoryStream MakeStream(int bytes) => new(new byte[bytes]);

    [Fact]
    public void Upload_Writes_File_And_Registers_Entry()
    {
        var r = _lib.Upload("hello.mp3", MakeStream(1024));
        File.Exists(Path.Combine(_tempDir, "sounds", "hello.mp3")).Should().BeTrue();
        _lib.Config.Sounds.Should().ContainSingle(s => s.Id == "hello");
        r.Entry.Position.Should().Be(new GridPosition(0, 0));
    }

    [Fact]
    public void Upload_Collision_Suffixes_2_3_etc()
    {
        _lib.Upload("foo.mp3", MakeStream(10));
        _lib.Upload("foo.mp3", MakeStream(10));
        _lib.Upload("foo.mp3", MakeStream(10));
        File.Exists(Path.Combine(_tempDir, "sounds", "foo.mp3")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "sounds", "foo (2).mp3")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "sounds", "foo (3).mp3")).Should().BeTrue();
    }

    [Fact]
    public void Upload_Id_Derives_From_Final_Filename_Not_Original()
    {
        _lib.Upload("raze ult.mp3", MakeStream(10));
        var second = _lib.Upload("raze ult.mp3", MakeStream(10));
        second.Entry.Id.Should().Be("raze-ult-2");
        second.Entry.File.Should().Be("raze ult (2).mp3");
    }

    [Fact]
    public void Upload_Rejects_Invalid_Extension()
    {
        Assert.Throws<InvalidOperationException>(() => _lib.Upload("evil.exe", MakeStream(10)));
    }

    [Fact]
    public void Upload_Rejects_Too_Large()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _lib.Upload("big.mp3", MakeStream((int)SoundLibrary.MaxUploadBytes + 1)));
    }

    [Fact]
    public void Upload_Concurrent_Same_Name_Produces_Two_Distinct_Files()
    {
        var t1 = Task.Run(() => _lib.Upload("dup.mp3", MakeStream(10)));
        var t2 = Task.Run(() => _lib.Upload("dup.mp3", MakeStream(10)));
        Task.WaitAll(t1, t2);
        Directory.GetFiles(Path.Combine(_tempDir, "sounds"), "dup*.mp3").Should().HaveCount(2);
    }

    [Fact]
    public void Upload_Sanitizes_Reserved_Name()
    {
        var r = _lib.Upload("CON.mp3", MakeStream(10));
        r.FinalFilename.Should().Be("_CON.mp3");
        r.Entry.File.Should().Be("_CON.mp3");
        r.Entry.Id.Should().Be("_con");
    }

    [Fact]
    public void Upload_Persists_Config_To_Disk()
    {
        _lib.Upload("foo.mp3", MakeStream(10));
        var raw = File.ReadAllText(Path.Combine(_tempDir, "config.json"));
        raw.Should().Contain("foo.mp3");
    }

    [Fact]
    public void Upload_Grid_Full_Expands_Rows()
    {
        _lib.MutateConfig(c => c with { Grid = new GridLayout(1, 1) });
        _lib.Upload("a.mp3", MakeStream(10));
        _lib.Upload("b.mp3", MakeStream(10));
        _lib.Config.Grid.Rows.Should().Be(2);
        _lib.Config.Sounds.Should().HaveCount(2);
    }
}
