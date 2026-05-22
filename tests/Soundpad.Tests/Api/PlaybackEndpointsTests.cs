using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Api.Dto;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

public class PlaybackEndpointsTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public PlaybackEndpointsTests(TestingWebApplicationFactory f) { _factory = f; }

    private HttpClient AuthedClient()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task GetState_Returns_Schema()
    {
        var c = AuthedClient();
        var s = await c.GetFromJsonAsync<StateDto>("/api/state");
        s.Should().NotBeNull();
        s!.AuthRequired.Should().BeTrue();
        s.Grid.Cols.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Play_Unknown_Returns_404()
    {
        var c = AuthedClient();
        var r = await c.PostAsync("/api/play/this-does-not-exist", null);
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Volume_Persists_To_Config()
    {
        var c = AuthedClient();
        var r = await c.PostAsJsonAsync("/api/volume", new { value = 33 });
        r.IsSuccessStatusCode.Should().BeTrue();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        lib.Config.Volume.Should().Be(33);
    }

    [Fact]
    public async Task MonitorDevice_Unknown_Returns_400()
    {
        var c = AuthedClient();
        var r = await c.PostAsJsonAsync("/api/monitor/device", new { device = "Definitely Not Real" });
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }
}
