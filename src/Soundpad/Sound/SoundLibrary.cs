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

    /// <summary>
    /// Raised after the in-memory config has been persisted via SaveLocked().
    /// Fires on whatever thread called Save/Upload/UpdateSound/etc — the WPF
    /// MainWindow subscribes and marshals the rebuild onto its Dispatcher.
    /// Handlers run OUTSIDE the library lock to avoid re-entrancy.
    /// </summary>
    public event Action? Changed;

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
        Changed?.Invoke();
    }

    private void SaveLocked()
    {
        var tmp = _opts.ConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(_config, Json);
        File.WriteAllText(tmp, json);
        if (File.Exists(_opts.ConfigPath)) File.Replace(tmp, _opts.ConfigPath, destinationBackupFileName: null);
        else File.Move(tmp, _opts.ConfigPath);
    }

    public const long MaxUploadBytes = 26_214_400;
    private static readonly string[] AllowedExtensions = { ".mp3", ".wav", ".ogg", ".flac" };

    public UploadResult Upload(string originalFilename, Stream content)
    {
        var ext = Path.GetExtension(originalFilename).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException($"Extension {ext} not allowed");
        if (content.CanSeek && content.Length > MaxUploadBytes)
            throw new InvalidOperationException("File too large");

        UploadResult result;
        lock (_lock)
        {
            Directory.CreateDirectory(_opts.SoundsDir);
            var sanitized = FilenameSanitizer.Sanitize(originalFilename, fallbackId: Guid.NewGuid().ToString("N"), extension: ext);
            var (fs, finalName) = CreateExclusive(sanitized);
            var savedConfig = _config;  // snapshot for rollback
            try
            {
                using (fs)
                {
                    content.CopyTo(fs);
                    if (fs.Length > MaxUploadBytes)
                        throw new InvalidOperationException("File too large");
                }

                var id = DeriveIdFromFilename(finalName, _config.Sounds);
                var grid = EnsureCellAvailable(_config.Grid, _config.Sounds);
                var pos = GridPositioner.NextFree(grid, _config.Sounds);
                var entry = new SoundEntry { Id = id, File = finalName, Label = id, Position = pos };
                _config = _config with { Grid = grid, Sounds = new List<SoundEntry>(_config.Sounds) { entry } };
                SaveLocked();
                result = new UploadResult(entry, finalName);
            }
            catch
            {
                // Roll back disk + memory state
                try { File.Delete(Path.Combine(_opts.SoundsDir, finalName)); } catch { /* best effort */ }
                _config = savedConfig;
                throw;
            }
        }
        Changed?.Invoke();
        return result;
    }

    private (FileStream Stream, string FileName) CreateExclusive(string sanitized)
    {
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        var ext = Path.GetExtension(sanitized);
        for (int i = 1; i <= 100; i++)
        {
            var candidate = i == 1 ? sanitized : $"{stem} ({i}){ext}";
            var full = Path.Combine(_opts.SoundsDir, candidate);
            try
            {
                var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
                return (fs, candidate);
            }
            catch (IOException) { /* try next */ }
        }
        throw new InvalidOperationException("Too many filename collisions");
    }

    private static string DeriveIdFromFilename(string filename, IReadOnlyList<SoundEntry> existing)
    {
        var stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        var slug = System.Text.RegularExpressions.Regex.Replace(stem, @"[^a-z0-9_]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "sound";
        var baseSlug = slug;
        var suffix = 'a';
        var ids = new HashSet<string>(existing.Select(e => e.Id));
        while (ids.Contains(slug))
        {
            slug = $"{baseSlug}-{suffix}";
            suffix = (char)(suffix + 1);
            if (suffix > 'z') throw new InvalidOperationException("Cannot derive unique id");
        }
        return slug;
    }

    private static GridLayout EnsureCellAvailable(GridLayout grid, IReadOnlyList<SoundEntry> sounds)
    {
        if (GridPositioner.NextFree(grid, sounds) is not null) return grid;
        return grid with { Rows = grid.Rows + 1 };
    }

    public void AutoScan()
    {
        bool changed = false;
        lock (_lock)
        {
            if (!Directory.Exists(_opts.SoundsDir)) return;
            var known = new HashSet<string>(_config.Sounds.Select(s => s.File), StringComparer.OrdinalIgnoreCase);
            var newEntries = new List<SoundEntry>();
            var grid = _config.Grid;
            var working = new List<SoundEntry>(_config.Sounds);
            foreach (var file in Directory.EnumerateFiles(_opts.SoundsDir).OrderBy(f => f))
            {
                var name = Path.GetFileName(file);
                if (known.Contains(name)) continue;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext)) continue;
                var id = DeriveIdFromFilename(name, working);
                grid = EnsureCellAvailable(grid, working);
                var pos = GridPositioner.NextFree(grid, working);
                var entry = new SoundEntry { Id = id, File = name, Label = id, Position = pos };
                working.Add(entry);
                newEntries.Add(entry);
            }
            if (newEntries.Count == 0 && grid == _config.Grid) return;
            _config = _config with { Grid = grid, Sounds = working };
            SaveLocked();
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    public void RepairInvariants()
    {
        bool changed = false;
        lock (_lock)
        {
            var repaired = GridPositioner.RepairDuplicates(_config.Grid, _config.Sounds);
            if (repaired.SequenceEqual(_config.Sounds)) return;
            _config = _config with { Sounds = repaired };
            SaveLocked();
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    public IReadOnlyList<SoundRuntimeStatus> GetRuntimeStatuses()
    {
        lock (_lock)
        {
            return _config.Sounds
                .Select(s => new SoundRuntimeStatus(s.Id, !File.Exists(Path.Combine(_opts.SoundsDir, s.File))))
                .ToList();
        }
    }

    public void UpdateSound(string id, string? label = null, string? color = null,
                            string? icon = null, GridPosition? position = null,
                            bool clearPosition = false)
    {
        lock (_lock)
        {
            var idx = _config.Sounds.ToList().FindIndex(s => s.Id == id);
            if (idx < 0) throw new InvalidOperationException($"Unknown sound id: {id}");

            // If a position move is requested, do the swap on the ORIGINAL list so
            // GridPositioner.Swap can read the moving entry's pre-move position to
            // hand it to the displaced occupant. Then apply field updates on top.
            List<SoundEntry> sounds;
            if (position is not null && !clearPosition)
            {
                sounds = GridPositioner.Swap(_config.Sounds, id, position);
            }
            else
            {
                sounds = _config.Sounds.ToList();
            }

            var i = sounds.FindIndex(s => s.Id == id);
            var current = sounds[i];
            sounds[i] = current with
            {
                Label = label ?? current.Label,
                Color = color ?? current.Color,
                Icon = icon ?? current.Icon,
                Position = clearPosition ? null : current.Position,
            };
            _config = _config with { Sounds = sounds };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void DeleteSound(string id, bool deleteFile)
    {
        lock (_lock)
        {
            var entry = _config.Sounds.FirstOrDefault(s => s.Id == id)
                ?? throw new InvalidOperationException($"Unknown sound id: {id}");
            if (deleteFile)
            {
                var path = Path.Combine(_opts.SoundsDir, entry.File);
                if (File.Exists(path)) File.Delete(path);
            }
            _config = _config with { Sounds = _config.Sounds.Where(s => s.Id != id).ToList() };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void ResizeGrid(int cols, int rows)
    {
        lock (_lock)
        {
            var newGrid = new GridLayout(cols, rows);
            var updated = _config.Sounds.Select(s =>
                s.Position is not null && !newGrid.Contains(s.Position)
                    ? s with { Position = null } : s).ToList();
            _config = _config with { Grid = newGrid, Sounds = updated };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void ApplyLayout(IEnumerable<(string Id, GridPosition? Position)> placements)
    {
        lock (_lock)
        {
            var list = placements.ToList();
            var nonNull = list.Where(p => p.Position is not null).ToList();
            if (nonNull.Select(p => p.Position).Distinct().Count() != nonNull.Count)
                throw new InvalidOperationException("Duplicate positions in layout");
            foreach (var (entryId, pos) in list)
                if (pos is not null && !_config.Grid.Contains(pos))
                    throw new InvalidOperationException($"Position {pos} out of grid {_config.Grid}");
            var map = list.ToDictionary(p => p.Id, p => p.Position);
            var updated = _config.Sounds
                .Select(s => map.TryGetValue(s.Id, out var p) ? s with { Position = p } : s)
                .ToList();
            _config = _config with { Sounds = updated };
            SaveLocked();
        }
        Changed?.Invoke();
    }
}
