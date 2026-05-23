using System.Security.Cryptography;
using Soundpad.Sound;

namespace Soundpad.Api;

/// <summary>
/// Token-management endpoints. The token sits in config.json and gates every
/// /api/* + /ws request via <see cref="Security.AuthTokenMiddleware"/>. Regen
/// is the only mutating operation here; rotating the token is the explicit
/// "kick all paired clients" affordance the user reaches for when they're
/// done sharing the URL with a friend or suspect the QR was screen-captured.
/// </summary>
public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/auth");

        // POST /api/auth/regenerate — requires a currently valid token (the
        // auth middleware enforces this). On success: generate a fresh 16-byte
        // hex token, persist it, return the new value. All currently connected
        // clients (phones, browsers, other tabs) hit 401 on their next request
        // and must re-scan the QR; that's the entire point of regeneration.
        g.MapPost("/regenerate", (SoundLibrary lib) =>
        {
            var newToken = GenerateToken();
            lib.MutateConfig(c => c with { AuthToken = newToken });
            lib.Save();
            // Note: we do NOT broadcast a libraryChanged here. The phone WS
            // connection authenticated with the old token will die naturally
            // on the next request — a broadcast would just race the 401.
            return Results.Ok(new { token = newToken });
        });
    }

    private static string GenerateToken()
    {
        // Match SoundConfig.GenerateToken: 16 bytes (128 bits) lower-hex.
        // Kept in sync deliberately so a regenerate produces a token that's
        // indistinguishable from a freshly-created config.
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
