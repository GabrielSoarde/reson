using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Api.Dto;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

/// <summary>
/// Covers the /api/boards/* surface plus the per-sound volume + move-to-board
/// helpers exposed by SoundEndpoints. All operations are scoped to the active
/// board unless otherwise noted (matches the SoundLibrary contract).
/// </summary>
public class BoardEndpointsTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;

    public BoardEndpointsTests(TestingWebApplicationFactory f) { _factory = f; }

    private HttpClient Auth()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    private SoundLibrary Lib => _factory.Services.GetRequiredService<SoundLibrary>();

    [Fact]
    public async Task GetBoards_Returns_Default_Board()
    {
        var c = Auth();
        var json = await c.GetFromJsonAsync<BoardsResponse>("/api/boards");
        json.Should().NotBeNull();
        json!.Boards.Should().NotBeEmpty();
        json.Boards.Should().Contain(b => b.Id == json.ActiveBoardId);
    }

    [Fact]
    public async Task PostBoards_Creates_New_Board()
    {
        var c = Auth();
        var uniqueName = "TestBoard-" + Guid.NewGuid().ToString("N")[..6];
        var r = await c.PostAsJsonAsync("/api/boards", new { name = uniqueName, color = "#22c55e" });
        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var b = await r.Content.ReadFromJsonAsync<BoardSummaryDto>();
        b.Should().NotBeNull();
        b!.Name.Should().Be(uniqueName);
        b.Color.Should().Be("#22c55e");
        Lib.Config.Boards.Should().Contain(x => x.Id == b.Id);
    }

    [Fact]
    public async Task PostBoards_Empty_Name_Returns_400()
    {
        var c = Auth();
        var r = await c.PostAsJsonAsync("/api/boards", new { name = "   " });
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutBoards_Renames_And_Recolors()
    {
        var c = Auth();
        var name = "Rename-" + Guid.NewGuid().ToString("N")[..6];
        var create = await c.PostAsJsonAsync("/api/boards", new { name });
        var created = await create.Content.ReadFromJsonAsync<BoardSummaryDto>();

        var r = await c.PutAsJsonAsync($"/api/boards/{created!.Id}", new { name = "Updated", color = "#ef4444" });
        r.IsSuccessStatusCode.Should().BeTrue();
        var updated = Lib.Config.Boards.Single(x => x.Id == created.Id);
        updated.Name.Should().Be("Updated");
        updated.Color.Should().Be("#ef4444");
    }

    [Fact]
    public async Task DeleteBoards_Removes_And_Switches_Active()
    {
        var c = Auth();
        var name = "Doomed-" + Guid.NewGuid().ToString("N")[..6];
        var create = await c.PostAsJsonAsync("/api/boards", new { name });
        var created = await create.Content.ReadFromJsonAsync<BoardSummaryDto>();
        await c.PostAsync($"/api/boards/{created!.Id}/activate", null);

        var r = await c.DeleteAsync($"/api/boards/{created.Id}");
        r.IsSuccessStatusCode.Should().BeTrue();
        Lib.Config.Boards.Should().NotContain(x => x.Id == created.Id);
        Lib.Config.ActiveBoardId.Should().NotBe(created.Id);
    }

    [Fact]
    public async Task DeleteBoards_Last_Board_Returns_400()
    {
        var c = Auth();
        // Force config down to a single board by deleting every non-default
        // board if any tests above leaked, then attempt to delete that last
        // one. The xUnit class fixture shares state between tests so we
        // explicitly probe via the API rather than poking the library.
        foreach (var b in Lib.Config.Boards.Skip(1).ToList())
            await c.DeleteAsync($"/api/boards/{b.Id}");
        var lastId = Lib.Config.Boards.Single().Id;
        var r = await c.DeleteAsync($"/api/boards/{lastId}");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostBoardActivate_Switches_Active_Board()
    {
        var c = Auth();
        var name = "ToActivate-" + Guid.NewGuid().ToString("N")[..6];
        var create = await c.PostAsJsonAsync("/api/boards", new { name });
        var created = await create.Content.ReadFromJsonAsync<BoardSummaryDto>();

        var r = await c.PostAsync($"/api/boards/{created!.Id}/activate", null);
        r.IsSuccessStatusCode.Should().BeTrue();
        Lib.Config.ActiveBoardId.Should().Be(created.Id);
    }

    [Fact]
    public async Task GetState_Includes_Boards_And_ActiveBoardId()
    {
        var c = Auth();
        var s = await c.GetFromJsonAsync<StateDto>("/api/state");
        s.Should().NotBeNull();
        s!.Boards.Should().NotBeNull();
        s.Boards!.Should().NotBeEmpty();
        s.ActiveBoardId.Should().NotBeNullOrEmpty();
        s.Boards.Should().Contain(b => b.Id == s.ActiveBoardId);
    }

    [Fact]
    public async Task PostSoundVolume_Updates_Per_Sound_Volume()
    {
        var c = Auth();
        // Need a sound on the active board first — drop one in via the
        // SoundLibrary so we don't depend on the upload pipeline's MIME guards.
        var lib = Lib;
        // Clean state: start with default board for this test.
        if (lib.Config.ActiveBoardId != lib.Config.Boards[0].Id)
            await c.PostAsync($"/api/boards/{lib.Config.Boards[0].Id}/activate", null);
        var soundId = $"volume-test-{Guid.NewGuid().ToString("N")[..6]}";
        var file = soundId + ".mp3";
        var soundsDir = Path.Combine(_factory.Services.GetRequiredService<Soundpad.AppOptions>().RootDir, "sounds");
        Directory.CreateDirectory(soundsDir);
        File.WriteAllBytes(Path.Combine(soundsDir, file), new byte[10]);
        lib.AutoScan();
        var entry = lib.ActiveBoard.Sounds.FirstOrDefault(s => s.File == file);
        entry.Should().NotBeNull();

        var r = await c.PostAsJsonAsync($"/api/sounds/{entry!.Id}/volume", new { value = 33 });
        r.IsSuccessStatusCode.Should().BeTrue();
        lib.ActiveBoard.Sounds.Single(s => s.Id == entry.Id).Volume.Should().Be(33);
    }

    [Fact]
    public async Task PostSoundMove_Moves_Sound_To_Other_Board()
    {
        var c = Auth();
        var lib = Lib;
        // Ensure default is active first.
        if (lib.Config.ActiveBoardId != lib.Config.Boards[0].Id)
            await c.PostAsync($"/api/boards/{lib.Config.Boards[0].Id}/activate", null);

        var soundId = $"move-test-{Guid.NewGuid().ToString("N")[..6]}";
        var file = soundId + ".mp3";
        var soundsDir = Path.Combine(_factory.Services.GetRequiredService<Soundpad.AppOptions>().RootDir, "sounds");
        Directory.CreateDirectory(soundsDir);
        File.WriteAllBytes(Path.Combine(soundsDir, file), new byte[10]);
        lib.AutoScan();
        var entry = lib.ActiveBoard.Sounds.FirstOrDefault(s => s.File == file);
        entry.Should().NotBeNull();

        var targetBoard = await CreateBoard(c, "MoveTarget-" + Guid.NewGuid().ToString("N")[..6]);
        var r = await c.PostAsJsonAsync($"/api/sounds/{entry!.Id}/move", new { boardId = targetBoard.Id });
        r.IsSuccessStatusCode.Should().BeTrue();
        lib.ActiveBoard.Sounds.Should().NotContain(s => s.File == file);
        lib.Config.Boards.Single(b => b.Id == targetBoard.Id)
            .Sounds.Should().Contain(s => s.File == file);
    }

    private async Task<BoardSummaryDto> CreateBoard(HttpClient c, string name)
    {
        var r = await c.PostAsJsonAsync("/api/boards", new { name });
        r.IsSuccessStatusCode.Should().BeTrue();
        var b = await r.Content.ReadFromJsonAsync<BoardSummaryDto>();
        return b!;
    }

    private record BoardsResponse(List<BoardSummaryDto> Boards, string ActiveBoardId);
}
