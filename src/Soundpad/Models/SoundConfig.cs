using System.Security.Cryptography;

namespace Soundpad.Models;

public record SoundConfig
{
    public int SchemaVersion { get; init; } = 1;
    public string AuthToken { get; init; } = "";
    public int Port { get; init; } = 8080;
    public string? PreferredNetworkAdapter { get; init; }
    public string? AudioDevice { get; init; }
    public string? MonitorDevice { get; init; }
    public bool MonitorEnabled { get; init; }
    public int Volume { get; init; } = 80;
    public int LatencyMs { get; init; } = 50;
    public GridLayout Grid { get; init; } = new(3, 4);
    public List<SoundEntry> Sounds { get; init; } = new();

    public static SoundConfig Default() => new()
    {
        AuthToken = GenerateToken(),
    };

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
