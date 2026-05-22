using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Soundpad.Api;

public class StateHub
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task AcceptAsync(HttpContext ctx)
    {
        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _sockets[id] = ws;
        try
        {
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buf, ctx.RequestAborted);
                if (res.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally
        {
            _sockets.TryRemove(id, out _);
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    public Task BroadcastAsync(string type, object payload, string? originId = null)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new WsEnvelope(type, originId, payload), Json));
        var segment = new ArraySegment<byte>(bytes);
        var tasks = _sockets.Values.Where(ws => ws.State == WebSocketState.Open)
            .Select(ws => ws.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None));
        return Task.WhenAll(tasks);
    }
}
