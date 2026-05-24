using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Soundpad.Security;

namespace Soundpad.Api;

public class StateHub
{
    // Per-socket SendLock serializes WebSocket.SendAsync calls on a single socket.
    // The underlying ManagedWebSocket implementation is not thread-safe for concurrent
    // sends — overlapping calls can interleave frame bytes and corrupt the protocol.
    // Multiple producers (audio engine thread, Kestrel request threads) can broadcast
    // simultaneously, so we serialize per socket. Sends to different sockets still
    // proceed in parallel (Task.WhenAll), which is the desired fan-out behavior.
    private readonly ConcurrentDictionary<Guid, (WebSocket Socket, SemaphoreSlim SendLock)> _sockets = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task AcceptAsync(HttpContext ctx)
    {
        var ws = ctx.WebSockets.WebSocketRequestedProtocols.Contains(AuthTokenMiddleware.WsAuthMarker)
            ? await ctx.WebSockets.AcceptWebSocketAsync(AuthTokenMiddleware.WsAuthMarker)
            : await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        var sendLock = new SemaphoreSlim(1, 1);
        _sockets[id] = (ws, sendLock);
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
            if (_sockets.TryRemove(id, out var entry))
                entry.SendLock.Dispose();
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    public Task BroadcastAsync(string type, object payload, string? originId = null)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new WsEnvelope(type, originId, payload), Json));
        var segment = new ArraySegment<byte>(bytes);
        var tasks = _sockets.Values
            .Where(e => e.Socket.State == WebSocketState.Open)
            .Select(e => SendOneAsync(e.Socket, e.SendLock, segment));
        return Task.WhenAll(tasks);
    }

    private static async Task SendOneAsync(WebSocket ws, SemaphoreSlim sendLock, ArraySegment<byte> segment)
    {
        // The semaphore may already be disposed if the socket was just removed in AcceptAsync's
        // finally block — swallow that race rather than crash the broadcaster.
        try { await sendLock.WaitAsync(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(segment, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (WebSocketException) { /* peer gone; AcceptAsync loop will clean up */ }
        catch (ObjectDisposedException) { /* socket disposed during send */ }
        finally
        {
            try { sendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }
}
