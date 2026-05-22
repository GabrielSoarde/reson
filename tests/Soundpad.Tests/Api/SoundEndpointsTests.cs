using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

public class SoundEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public SoundEndpointsTests(WebApplicationFactory<Program> f) { _factory = f; }

    private HttpClient Auth()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task Upload_Bad_Extension_Returns_400()
    {
        var c = Auth();
        var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(new byte[10]);
        bytes.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        content.Add(bytes, "file", "evil.exe");
        var r = await c.PostAsync("/api/sounds/upload", content);
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grid_Layout_Duplicate_Positions_Returns_400()
    {
        var c = Auth();
        // need two known sounds first
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        if (lib.Config.Sounds.Count < 2) { return; }
        var ids = lib.Config.Sounds.Take(2).Select(s => s.Id).ToList();
        var body = new
        {
            placements = new[]
            {
                new { id = ids[0], position = new { col = 0, row = 0 } },
                new { id = ids[1], position = new { col = 0, row = 0 } },
            }
        };
        var r = await c.PostAsJsonAsync("/api/grid/layout", body);
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grid_Resize_Updates_State()
    {
        var c = Auth();
        var r = await c.PostAsJsonAsync("/api/grid", new { cols = 4, rows = 5 });
        r.IsSuccessStatusCode.Should().BeTrue();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        lib.Config.Grid.Cols.Should().Be(4);
        lib.Config.Grid.Rows.Should().Be(5);
    }
}
