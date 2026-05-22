using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Api;
using Soundpad.Sound;

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

    [Fact]
    public async Task OriginId_From_Request_Header_Roundtrips_Into_WS_Envelope()
    {
        // Verify Fix 2: a POST /api/volume with X-Origin-Id: abc should produce a
        // volumeChanged envelope whose originId == "abc". This is what enables the
        // browser-side echo filter to ignore its own mutations.
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        var token = lib.Config.AuthToken;

        var wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new Uri(_factory.Server.BaseAddress, $"/ws?t={token}");
        using var ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        // Fire the mutation with our origin id.
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Auth-Token", token);
        http.DefaultRequestHeaders.Add("X-Origin-Id", "abc");
        var resp = await http.PostAsJsonAsync("/api/volume", new { value = 42 });
        resp.IsSuccessStatusCode.Should().BeTrue();

        // Receive the first frame and inspect the envelope.
        var env = await ReceiveEnvelope(ws, TimeSpan.FromSeconds(5));
        env.Type.Should().Be("volumeChanged");
        env.OriginId.Should().Be("abc");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task OriginId_Absent_Header_Yields_Null_OriginId_In_Envelope()
    {
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        var token = lib.Config.AuthToken;

        var wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new Uri(_factory.Server.BaseAddress, $"/ws?t={token}");
        using var ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Auth-Token", token);
        // No X-Origin-Id header.
        var resp = await http.PostAsJsonAsync("/api/monitor", new { enabled = true });
        resp.IsSuccessStatusCode.Should().BeTrue();

        var env = await ReceiveEnvelope(ws, TimeSpan.FromSeconds(5));
        env.Type.Should().Be("monitorChanged");
        env.OriginId.Should().BeNull();

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task BroadcastAsync_Handles_Many_Concurrent_Sends_Without_Corruption()
    {
        // Verify Fix 1: serialize per-socket sends. We spam BroadcastAsync from many
        // threads in parallel and assert that (a) no exception escapes and (b) every
        // message arrives intact and parseable. A non-thread-safe SendAsync would
        // interleave frame bytes and the receiver would observe protocol errors or
        // garbled JSON.
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        var hub = _factory.Services.GetRequiredService<StateHub>();
        var token = lib.Config.AuthToken;

        var wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new Uri(_factory.Server.BaseAddress, $"/ws?t={token}");
        using var ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        const int total = 100;
        // Start the reader before producers so the SendAsync buffer doesn't fill.
        var received = new List<WsEnvelope>(total);
        var readerCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reader = Task.Run(async () =>
        {
            while (received.Count < total && !readerCts.IsCancellationRequested)
            {
                var env = await ReceiveEnvelope(ws, TimeSpan.FromSeconds(10));
                received.Add(env);
            }
        });

        // Fan out concurrent broadcasts.
        var producers = Enumerable.Range(0, total)
            .Select(i => Task.Run(() => hub.BroadcastAsync("playing", new { soundId = $"s-{i}" })))
            .ToArray();
        await Task.WhenAll(producers);
        await reader;

        received.Should().HaveCount(total);
        received.Should().OnlyContain(e => e.Type == "playing");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    private static async Task<WsEnvelope> ReceiveEnvelope(WebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var res = await ws.ReceiveAsync(buf, cts.Token);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WS closed before a message arrived");
            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
            if (res.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<WsEnvelope>(sb.ToString(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("Failed to parse envelope");
    }
}
