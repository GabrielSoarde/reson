using System.Security.Cryptography;

namespace Soundpad.Models;

public record SoundConfig
{
    // v1 → v2 (2026-05): AudioDevice / MonitorDevice / MicDevice now hold
    // stable WASAPI endpoint ids ({0.0.0.x}.{guid}) instead of FriendlyNames.
    // SoundLibrary.Load migrates v1 configs in place (resolves names to ids
    // via DeviceLocator on first launch after upgrade).
    public int SchemaVersion { get; init; } = 2;
    public string AuthToken { get; init; } = "";
    public int Port { get; init; } = 8080;
    public string? PreferredNetworkAdapter { get; init; }
    public string? AudioDevice { get; init; }
    public string? MonitorDevice { get; init; }
    public string? MicDevice { get; init; }
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
