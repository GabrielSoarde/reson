using System.Text.Json;
using System.Text.Json.Serialization;
using Soundpad.Models;

namespace Soundpad.Sound;

public class SoundLibrary
{
    private const int CurrentSchemaVersion = 1;
    private readonly SoundLibraryOptions _opts;
    private readonly object _lock = new();
    private SoundConfig _config = SoundConfig.Default();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SoundLibrary(SoundLibraryOptions opts) { _opts = opts; }

    public SoundConfig Config { get { lock (_lock) { return _config; } } }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_opts.ConfigPath))
            {
                _config = SoundConfig.Default();
                SaveLocked();
                return;
            }
            var json = File.ReadAllText(_opts.ConfigPath);
            var loaded = JsonSerializer.Deserialize<SoundConfig>(json, Json)
                ?? throw new InvalidDataException("config.json was empty/null");
            if (loaded.SchemaVersion > CurrentSchemaVersion)
                throw new NotSupportedException(
                    $"config.json schemaVersion {loaded.SchemaVersion} is newer than supported {CurrentSchemaVersion}");
            _config = loaded;
        }
    }

    public void MutateConfig(Func<SoundConfig, SoundConfig> mutate)
    {
        lock (_lock) { _config = mutate(_config); }
    }

    public void Save()
    {
        lock (_lock) { SaveLocked(); }
    }

    private void SaveLocked()
    {
        var tmp = _opts.ConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(_config, Json);
        File.WriteAllText(tmp, json);
        if (File.Exists(_opts.ConfigPath)) File.Replace(tmp, _opts.ConfigPath, destinationBackupFileName: null);
        else File.Move(tmp, _opts.ConfigPath);
    }
}
