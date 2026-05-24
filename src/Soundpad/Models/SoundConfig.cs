using System.Security.Cryptography;

namespace Soundpad.Models;

public record SoundConfig
{
    // v1 → v2 (2026-05): AudioDevice / MonitorDevice / MicDevice now hold
    // stable WASAPI endpoint ids ({0.0.0.x}.{guid}) instead of FriendlyNames.
    // SoundLibrary.Load migrates v1 configs in place (resolves names to ids
    // via DeviceLocator on first launch after upgrade).
    // v2 → v3 (2026-05): SoundEntry gains PlayCount + LastPlayedAt usage
    // stats. Pure schema bump — new fields default to 0 / null via record
    // initializers, so existing v2 entries deserialize cleanly.
    // v3 → v4 (2026-05): multi-board support. The top-level Grid + Sounds
    // collapse into a Boards list; SoundEntry gains Volume (0-100). Load()
    // wraps a v3 config in a single "default" board to preserve all sounds
    // and the user's grid dimensions.
    // v4 → v5 (2026-05): additive — SoundEntry gains NormalizeGainDb (nullable,
    // default null) and SoundConfig gains NormalizeEnabled (default true). No
    // data transform needed; deserializer fills defaults + stamp-forward handles it.
    public int SchemaVersion { get; init; } = 5;
    public string AuthToken { get; init; } = "";
    public int Port { get; init; } = 8080;
    public string? PreferredNetworkAdapter { get; init; }
    public string? AudioDevice { get; init; }
    public string? MonitorDevice { get; init; }
    public string? MicDevice { get; init; }
    public bool MonitorEnabled { get; init; }
    // Schema v5: global toggle for loudness normalization. Default on so
    // sounds equalize out of the box; user can disable to hear raw levels.
    public bool NormalizeEnabled { get; init; } = true;
    public int Volume { get; init; } = 80;
    public int LatencyMs { get; init; } = 50;
    // Schema v4: boards replace the single top-level Grid+Sounds. Default
    // config starts with one board called "Padrão" — the user can rename it
    // or add more from the WPF Boards panel.
    public List<Board> Boards { get; init; } = new();
    public string ActiveBoardId { get; init; } = "";

    public static SoundConfig Default()
    {
        var board = new Board { Id = "default", Name = "Padrão", Color = "#3b82f6" };
        return new SoundConfig
        {
            AuthToken = GenerateToken(),
            Boards = new List<Board> { board },
            ActiveBoardId = board.Id,
        };
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
