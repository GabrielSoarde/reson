using System.Security.Cryptography;
using System.Text;
using Soundpad.Sound;

namespace Soundpad.Security;

public class AuthTokenMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SoundLibrary _library;

    public AuthTokenMiddleware(RequestDelegate next, SoundLibrary library)
    {
        _next = next;
        _library = library;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var needsAuth = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/ws", StringComparison.OrdinalIgnoreCase);

        if (!needsAuth)
        {
            await _next(ctx);
            return;
        }

        var supplied = ctx.Request.Headers["X-Auth-Token"].FirstOrDefault()
                       ?? ctx.Request.Query["t"].FirstOrDefault();

        if (supplied is null || !TokensMatch(supplied, _library.Config.AuthToken))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_token" });
            return;
        }

        await _next(ctx);
    }

    private static bool TokensMatch(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
