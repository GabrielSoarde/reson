using FluentAssertions;

namespace Soundpad.Tests.Api;

public class StateHubTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public StateHubTests(TestingWebApplicationFactory f) { _factory = f; }

    [Fact]
    public async Task WS_Without_Token_Rejected_With_401()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/ws"); // not a WS handshake, just confirm 401 path
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
