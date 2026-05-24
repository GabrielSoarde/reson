using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

public class NormalizeEndpointTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public NormalizeEndpointTests(TestingWebApplicationFactory f) { _factory = f; }

    private HttpClient Auth()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task Post_Normalize_Toggles_And_Persists()
    {
        var c = Auth();
        var r = await c.PostAsJsonAsync("/api/normalize", new { enabled = false });
        r.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<SoundLibrary>().Config.NormalizeEnabled.Should().BeFalse();
    }
}
