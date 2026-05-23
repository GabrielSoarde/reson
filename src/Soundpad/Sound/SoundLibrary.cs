using System.Text.Json;
using System.Text.Json.Serialization;
using Soundpad.Audio;
using Soundpad.Models;

namespace Soundpad.Sound;

public class SoundLibrary
{
    // Bumped to 3: SoundEntry gained PlayCount + LastPlayedAt usage stats.
    // v1 → v2 migration: device fields hold endpoint ids instead of
    // FriendlyNames — see MigrateDeviceIdentifiers below.
    // v2 → v3 migration: pure schema bump — happens automatically inside
    // Load() since the new SoundEntry fields default to 0 / null.
    private const int CurrentSchemaVersion = 3;
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

            // v2 → v3 auto-migration: pure schema bump (new SoundEntry fields
            // default to 0 / null via record initializers). The v1 → v2 device
            // migration still happens separately in MigrateDeviceIdentifiers
            // because it needs an IAudioDeviceEnumerator. Bumping the schema
            // version here makes the file roundtrip cleanly on next save.
            if (_config.SchemaVersion == 2)
            {
                _config = _config with { SchemaVersion = 3 };
                SaveLocked();
            }
        }
    }

    /// <summary>
    /// One-shot migration of v1 configs (device fields = FriendlyName) to v2
    /// (device fields = WASAPI endpoint id). For each populated device field
    /// we try to resolve the legacy FriendlyName to a current device id; if
    /// the device is still present, we keep it under its stable id. If the
    /// device is gone (renamed/unplugged at the time of upgrade) the field
    /// is set to null and the user re-picks it from the UI.
    ///
    /// Idempotent: schemaVersion is bumped to 2 and a no-op on next launch.
    /// Silent: only the schema bump (and any id translation) is written; no
    /// user-facing message. Safe to call after <see cref="Load"/>.
    /// </summary>
    public void MigrateDeviceIdentifiers(DeviceLocator locator)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_config.SchemaVersion >= 2) return;

            string? Translate(string? legacyName)
            {
                if (string.IsNullOrEmpty(legacyName)) return null;
                // If the persisted value is already an endpoint id (rare for
                // a v1 config, but defensive against partially-migrated
                // states), keep it.
                if (locator.DeviceExists(legacyName)) return legacyName;
                return locator.FindIdByName(legacyName);
            }

            var migrated = _config with
            {
                // Jump straight to current schema (v3). v2 → v3 is a pure
                // bump (new SoundEntry fields default to 0 / null) so we
                // don't need a separate pass after the device translation.
                SchemaVersion = CurrentSchemaVersion,
                AudioDevice = Translate(_config.AudioDevice),
                MonitorDevice = Translate(_config.MonitorDevice),
                MicDevice = Translate(_config.MicDevice),
            };

            if (!migrated.Equals(_config))
            {
                _config = migrated;
                changed = true;
            }
            else
            {
                // Schema-only bump (all device fields were null) — still persist
                // so we don't re-run the migration on every launch.
                _config = migrated;
                changed = true;
            }
            SaveLocked();
        }
        if (changed) Changed?.Invoke();
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

    /// <summary>
    /// Raise <see cref="Changed"/> without mutating config — used by
    /// <see cref="SoundsFolderWatcher"/> when an external delete happens
    /// (the entry stays in config but its runtime status flips to missing,
    /// and the UI must rebuild to reflect that).
    /// </summary>
    public void NotifyExternalChange() => Changed?.Invoke();

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

    /// <summary>
    /// Record a successful play of <paramref name="soundId"/>: increment
    /// <see cref="SoundEntry.PlayCount"/> and stamp
    /// <see cref="SoundEntry.LastPlayedAt"/> = <c>DateTime.UtcNow</c>, then
    /// persist + raise <see cref="Changed"/>. Called by
    /// <see cref="Audio.PlaybackEngine"/> from inside HandlePlay after the
    /// debounce check, so phantom plays during library mutations (delete in
    /// flight, etc.) won't crash — an unknown id is a no-op.
    ///
    /// Thread-safe via the existing <c>_lock</c>. The engine's 80 ms debounce
    /// naturally caps the write frequency from mic-spam scenarios.
    /// </summary>
    public void RecordPlay(string soundId)
    {
        bool changed = false;
        lock (_lock)
        {
            var sounds = _config.Sounds.ToList();
            var idx = sounds.FindIndex(s => s.Id == soundId);
            if (idx < 0) return; // unknown id (mid-delete race) — no-op, no throw
            sounds[idx] = sounds[idx] with
            {
                PlayCount = sounds[idx].PlayCount + 1,
                LastPlayedAt = DateTime.UtcNow,
            };
            _config = _config with { Sounds = sounds };
            SaveLocked();
            changed = true;
        }
        if (changed) Changed?.Invoke();
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
