using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Sound;

namespace Soundpad.Tests.Security;

public class AuthTokenTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthTokenTests(TestingWebApplicationFactory f)
    {
        _factory = f;
        _client = f.CreateClient();
    }

    [Fact]
    public async Task Api_Without_Token_Returns_401()
    {
        var r = await _client.GetAsync("/api/state");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Root_Is_Public()
    {
        // wwwroot/index.html does not exist yet (Task 23 wires the frontend), so the
        // endpoint will not return a 2xx. The relevant assertion for this task is that
        // the request is NOT short-circuited by the auth middleware.
        var r = await _client.GetAsync("/");
        r.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_Rejects_Token_In_Query_String()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        var r = await c.GetAsync($"/api/state?t={lib.Config.AuthToken}"); // query, NO header
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Api_Accepts_Token_In_Header()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        (await c.GetAsync("/api/state")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
