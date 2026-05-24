using System.Net.WebSockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Sound;

namespace Soundpad.Tests.Security;

public class WsSubprotocolAuthTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public WsSubprotocolAuthTests(TestingWebApplicationFactory f) { _factory = f; }

    private string Token => _factory.Services.GetRequiredService<SoundLibrary>().Config.AuthToken;

    [Fact]
    public async Task Ws_Connects_With_Token_In_Subprotocol_No_Query()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
            req.Headers["Sec-WebSocket-Protocol"] = $"reson.auth.v1, {Token}";
        var ws = await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, "ws"), default);
        ws.SubProtocol.Should().Be("reson.auth.v1");   // server echoed the marker, NOT the token
        ws.State.Should().Be(WebSocketState.Open);
        // Abort rather than CloseAsync: the in-memory TestServer scheduler can deadlock
        // on a graceful close handshake when the server receive loop is awaiting on the
        // same synchronization context. Abort tears down the transport without the
        // round-trip, letting AcceptAsync exit via RequestAborted.
        ws.Abort();
    }

    [Fact]
    public async Task Ws_Rejects_Bad_Token_In_Subprotocol()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
            req.Headers["Sec-WebSocket-Protocol"] = "reson.auth.v1, deadbeef";
        var act = async () => await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, "ws"), default);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("401"));  // 401 → handshake fails
    }

    [Fact]
    public async Task Ws_Rejects_Marker_Only_No_Token()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
            req.Headers["Sec-WebSocket-Protocol"] = "reson.auth.v1"; // marker, but NO token entry
        var act = async () => await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, "ws"), default);
        // Pins the no-bypass guarantee: FirstOrDefault(p => p != marker) returns null,
        // which must NOT authenticate — the handshake must be rejected with 401.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("401"));  // 401 → no open socket, no teardown needed
    }

    [Fact]
    public async Task Ws_Still_Accepts_Legacy_Query_Token()
    {
        var wsClient = _factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri(_factory.Server.BaseAddress, $"ws?t={Token}"), default);
        ws.State.Should().Be(WebSocketState.Open);
        // Abort rather than CloseAsync: avoids the TestServer scheduler deadlock
        // described in Ws_Connects_With_Token_In_Subprotocol_No_Query.
        ws.Abort();
    }
}
