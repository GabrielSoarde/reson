using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Soundpad.Tests.Security;

public class AuthTokenTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AuthTokenTests(WebApplicationFactory<Program> f)
    {
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
}
