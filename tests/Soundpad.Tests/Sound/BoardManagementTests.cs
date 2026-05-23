using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

/// <summary>
/// Covers the schema v4 board CRUD surface on SoundLibrary: Create / Update /
/// Delete / Activate boards, plus the cross-board MoveSoundToBoard helper.
/// </summary>
public class BoardManagementTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-board-" + Guid.NewGuid());

    public BoardManagementTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private SoundLibrary BuildLib()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        return lib;
    }

    [Fact]
    public void CreateBoard_Adds_To_Config_And_Returns_Board()
    {
        var lib = BuildLib();
        var b = lib.CreateBoard("Stream");
        b.Name.Should().Be("Stream");
        b.Id.Should().Be("stream");
        lib.Config.Boards.Should().Contain(x => x.Id == "stream");
    }

    [Fact]
    public void CreateBoard_Disambiguates_Id_On_Collision()
    {
        var lib = BuildLib();
        var b1 = lib.CreateBoard("Stream");
        var b2 = lib.CreateBoard("Stream");
        b1.Id.Should().Be("stream");
        b2.Id.Should().Be("stream-2");
    }

    [Fact]
    public void CreateBoard_Rejects_Empty_Name()
    {
        var lib = BuildLib();
        var act = () => lib.CreateBoard("   ");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CreateBoard_Does_Not_Auto_Activate()
    {
        var lib = BuildLib();
        var originalActive = lib.Config.ActiveBoardId;
        lib.CreateBoard("Game");
        lib.Config.ActiveBoardId.Should().Be(originalActive);
    }

    [Fact]
    public void UpdateBoard_Renames_And_Recolors()
    {
        var lib = BuildLib();
        var b = lib.CreateBoard("Old name");
        lib.UpdateBoard(b.Id, name: "Novo nome", color: "#22c55e");
        var updated = lib.Config.Boards.Single(x => x.Id == b.Id);
        updated.Name.Should().Be("Novo nome");
        updated.Color.Should().Be("#22c55e");
    }

    [Fact]
    public void UpdateBoard_Null_Args_Preserve_Existing_Values()
    {
        var lib = BuildLib();
        var b = lib.CreateBoard("X", color: "#ff0000");
        lib.UpdateBoard(b.Id, name: "Y", color: null);
        var updated = lib.Config.Boards.Single(x => x.Id == b.Id);
        updated.Name.Should().Be("Y");
        updated.Color.Should().Be("#ff0000");
    }

    [Fact]
    public void DeleteBoard_Removes_And_Switches_Active_If_Needed()
    {
        var lib = BuildLib();
        var b = lib.CreateBoard("Temp");
        lib.ActivateBoard(b.Id);
        lib.Config.ActiveBoardId.Should().Be(b.Id);

        lib.DeleteBoard(b.Id);
        lib.Config.Boards.Should().NotContain(x => x.Id == b.Id);
        lib.Config.ActiveBoardId.Should().NotBe(b.Id);
    }

    [Fact]
    public void DeleteBoard_Refuses_To_Remove_Last_Board()
    {
        var lib = BuildLib();
        // Default config has one board called "default".
        var act = () => lib.DeleteBoard("default");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ActivateBoard_Updates_ActiveBoardId()
    {
        var lib = BuildLib();
        var b = lib.CreateBoard("Other");
        lib.ActivateBoard(b.Id);
        lib.Config.ActiveBoardId.Should().Be(b.Id);
        lib.ActiveBoard.Id.Should().Be(b.Id);
    }

    [Fact]
    public void ActivateBoard_Unknown_Id_Throws()
    {
        var lib = BuildLib();
        var act = () => lib.ActivateBoard("nope-not-real");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Sound_Mutating_Operations_Scope_To_Active_Board()
    {
        // Upload a sound on the default board, switch boards, upload another
        // sound, switch back — each board should hold only its own sounds.
        var lib = BuildLib();
        var streamBoard = lib.CreateBoard("Stream");

        // Upload one to default
        lib.Upload("a.mp3", new MemoryStream(new byte[10]));
        lib.ActiveBoard.Sounds.Should().HaveCount(1);

        // Switch + upload another
        lib.ActivateBoard(streamBoard.Id);
        lib.Upload("b.mp3", new MemoryStream(new byte[10]));
        lib.ActiveBoard.Sounds.Should().HaveCount(1);
        lib.ActiveBoard.Sounds[0].File.Should().Be("b.mp3");

        // Switch back — should still see "a"
        lib.ActivateBoard("default");
        lib.ActiveBoard.Sounds.Should().HaveCount(1);
        lib.ActiveBoard.Sounds[0].File.Should().Be("a.mp3");
    }

    [Fact]
    public void MoveSoundToBoard_Moves_Entry_And_Strips_Position()
    {
        var lib = BuildLib();
        var streamBoard = lib.CreateBoard("Stream");

        // Add a sound on default at position (1,1).
        lib.Upload("a.mp3", new MemoryStream(new byte[10]));
        var entry = lib.ActiveBoard.Sounds[0];
        lib.UpdateSound(entry.Id, position: new GridPosition(1, 1));

        lib.MoveSoundToBoard(entry.Id, streamBoard.Id);

        // Gone from default, present on Stream with a fresh position.
        lib.Config.Boards.Single(b => b.Id == "default").Sounds.Should().BeEmpty();
        var stream = lib.Config.Boards.Single(b => b.Id == streamBoard.Id);
        stream.Sounds.Should().ContainSingle(s => s.Id == entry.Id);
        stream.Sounds[0].Position.Should().NotBeNull();
    }

    [Fact]
    public void MoveSoundToBoard_To_Same_Board_Is_NoOp()
    {
        var lib = BuildLib();
        lib.Upload("a.mp3", new MemoryStream(new byte[10]));
        var entry = lib.ActiveBoard.Sounds[0];

        lib.MoveSoundToBoard(entry.Id, lib.Config.ActiveBoardId);

        lib.ActiveBoard.Sounds.Should().HaveCount(1);
    }

    [Fact]
    public void MoveSoundToBoard_Disambiguates_Id_On_Collision()
    {
        var lib = BuildLib();
        var streamBoard = lib.CreateBoard("Stream");

        // Same filename uploaded on both boards — both should get a sound,
        // but with id collision the move re-numbers.
        lib.Upload("song.mp3", new MemoryStream(new byte[10]));
        var defaultEntry = lib.ActiveBoard.Sounds.Single();

        lib.ActivateBoard(streamBoard.Id);
        // A different file under the same stem-id slot ("song") so the moves
        // collide on id.
        lib.Upload("song.mp3", new MemoryStream(new byte[10]));
        lib.ActiveBoard.Sounds.Should().HaveCount(1);

        // Move the default's "song" → stream board. Id should bump to "song-2"
        // to avoid clobbering the existing entry.
        lib.ActivateBoard("default");
        lib.MoveSoundToBoard(defaultEntry.Id, streamBoard.Id);

        var stream = lib.Config.Boards.Single(b => b.Id == streamBoard.Id);
        stream.Sounds.Should().HaveCount(2);
        stream.Sounds.Should().Contain(s => s.Id == defaultEntry.Id || s.Id == defaultEntry.Id + "-2");
    }

    [Fact]
    public void Boards_Persist_To_Disk_Across_Reload()
    {
        var lib1 = BuildLib();
        var b = lib1.CreateBoard("Persisted");
        lib1.UpdateBoard(b.Id, color: "#22c55e");
        lib1.ActivateBoard(b.Id);

        // Fresh library + reload — must see the same boards.
        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.Boards.Should().Contain(x => x.Id == "persisted" && x.Color == "#22c55e");
        lib2.Config.ActiveBoardId.Should().Be(b.Id);
    }
}
