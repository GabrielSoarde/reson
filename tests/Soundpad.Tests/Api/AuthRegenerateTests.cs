using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

/// <summary>
/// Coverage for POST /api/auth/regenerate. The endpoint is small but the UX
/// invariant is load-bearing: rotating the token must invalidate the old one
/// immediately so a shared QR can be cut off. All three assertions below
/// (auth required, new token shape, persisted to library) ride on top of
/// the same auth middleware that gates every other /api route.
/// </summary>
public class AuthRegenerateTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public AuthRegenerateTests(TestingWebApplicationFactory f) { _factory = f; }

    private HttpClient Authed()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task Regenerate_Without_Auth_Returns_401()
    {
        // No X-Auth-Token header → middleware rejects before reaching the
        // endpoint handler. Keeps the regen action gated to the legitimate
        // operator (anyone holding a current token, e.g. via the WPF sidebar).
        var c = _factory.CreateClient();
        var r = await c.PostAsync("/api/auth/regenerate", content: null);
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Regenerate_Returns_New_Token_Of_Expected_Shape()
    {
        var c = Authed();
        var r = await c.PostAsync("/api/auth/regenerate", content: null);
        r.IsSuccessStatusCode.Should().BeTrue();
        var body = await r.Content.ReadFromJsonAsync<TokenResponse>();
        body.Should().NotBeNull();
        // 16-byte hex = 32 lowercase hex chars. The format is part of the
        // contract: callers (WPF sidebar) display it inside a QR / URL and
        // assume it's URL-safe without escaping.
        body!.Token.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Regenerate_Persists_New_Token_And_Old_Is_Rejected()
    {
        // Snapshot the current token before regen so we can prove the old
        // value is gone from the library AND that the middleware rejects it.
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        var oldToken = lib.Config.AuthToken;

        var c = Authed();
        var r = await c.PostAsync("/api/auth/regenerate", content: null);
        r.IsSuccessStatusCode.Should().BeTrue();
        var body = await r.Content.ReadFromJsonAsync<TokenResponse>();

        // Library state mutated.
        lib.Config.AuthToken.Should().Be(body!.Token);
        lib.Config.AuthToken.Should().NotBe(oldToken);

        // Old token now produces 401 on any /api route — this is the entire
        // point of the regenerate action ("kick all paired clients").
        using var stale = _factory.CreateClient();
        stale.DefaultRequestHeaders.Add("X-Auth-Token", oldToken);
        var staleResp = await stale.GetAsync("/api/state");
        staleResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // And the new token works.
        using var fresh = _factory.CreateClient();
        fresh.DefaultRequestHeaders.Add("X-Auth-Token", body.Token);
        var freshResp = await fresh.GetAsync("/api/state");
        freshResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private record TokenResponse(string Token);
}
