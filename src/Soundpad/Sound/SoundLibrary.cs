using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Soundpad.Audio;
using Soundpad.Models;

namespace Soundpad.Sound;

public class SoundLibrary
{
    // Bumped to 5: schema v5 adds NormalizeGainDb (SoundEntry) + NormalizeEnabled
    // (SoundConfig). Both are additive with safe defaults; stamp-forward handles them.
    // v1 → v2 migration: device fields hold endpoint ids instead of
    // FriendlyNames — see MigrateDeviceIdentifiers below.
    // v2 → v3 migration: pure schema bump (SoundEntry gained PlayCount +
    // LastPlayedAt) — happens automatically inside Load() since the new fields
    // default to 0 / null via record initializers.
    // v3 → v4 migration: wrap the old top-level Grid+Sounds in a single
    // "default" board. Happens inside Load() before deserializing into the
    // strongly-typed SoundConfig so existing on-disk shapes still round-trip.
    // v4 → v5 migration: pure schema bump (additive fields with defaults).
    private const int CurrentSchemaVersion = 5;
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
    /// Convenience accessor: the currently-active <see cref="Board"/>. All
    /// existing sound-mutating endpoints scope to this board so legacy callers
    /// (mobile/web clients that don't know about boards yet) keep working
    /// transparently.
    /// </summary>
    public Board ActiveBoard
    {
        get
        {
            lock (_lock) { return ResolveActiveBoardLocked(_config); }
        }
    }

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
                // First run only: seed the default board with whatever audio is
                // already in the sounds/ folder (the installer drops samples there).
                // This is the ONLY place a blanket folder-scan runs — afterwards
                // sounds are added explicitly, so removed-but-kept files are not
                // re-imported.
                SeedFromFolderLocked();
                return;
            }
            var json = File.ReadAllText(_opts.ConfigPath);

            // Pre-parse as a generic JsonObject so we can run the v3 → v4
            // structural migration (wrap top-level Grid + Sounds in a default
            // Board) BEFORE strongly-typed deserialization — SoundConfig no
            // longer has those properties so a direct Deserialize<SoundConfig>
            // would drop the user's sound list.
            var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("config.json was empty/null");

            int sv = root["schemaVersion"]?.GetValue<int>() ?? 1;
            if (sv > CurrentSchemaVersion)
                throw new NotSupportedException(
                    $"config.json schemaVersion {sv} is newer than supported {CurrentSchemaVersion}");

            bool dirtyFromStructural = false;
            if (sv < 4)
            {
                MigrateV3StructureToV4(root);
                root["schemaVersion"] = 4;
                dirtyFromStructural = true;
            }

            var loaded = root.Deserialize<SoundConfig>(Json)
                ?? throw new InvalidDataException("config.json deserialized to null");

            // Defensive: a hand-edited v4 config might omit boards or set an
            // activeBoardId that points nowhere. Backfill a default board so the
            // rest of the engine has something to operate on.
            if (loaded.Boards.Count == 0)
            {
                var b = new Board { Id = "default", Name = "Padrão", Color = "#3b82f6" };
                loaded = loaded with { Boards = new List<Board> { b }, ActiveBoardId = b.Id };
                dirtyFromStructural = true;
            }
            if (string.IsNullOrEmpty(loaded.ActiveBoardId) ||
                !loaded.Boards.Any(b => b.Id == loaded.ActiveBoardId))
            {
                loaded = loaded with { ActiveBoardId = loaded.Boards[0].Id };
                dirtyFromStructural = true;
            }

            _config = loaded;

            // v2 → v3 auto-migration (pure schema bump for SoundEntry usage
            // stats). The strongly-typed deserialize already filled in defaults
            // for any missing PlayCount/LastPlayedAt fields — we just need to
            // stamp the schema version forward so the file round-trips cleanly.
            if (_config.SchemaVersion < CurrentSchemaVersion)
            {
                _config = _config with { SchemaVersion = CurrentSchemaVersion };
                dirtyFromStructural = true;
            }

            if (dirtyFromStructural)
            {
                SaveLocked();
            }
        }
    }

    /// <summary>
    /// In-place structural migration of a pre-v4 (v1/v2/v3) config JSON object
    /// to the v4 shape: lift the top-level "grid" + "sounds" into a single
    /// board called "Padrão" and add "boards" + "activeBoardId" fields. Leaves
    /// every other top-level field (device ids, token, port, etc.) untouched.
    /// </summary>
    private static void MigrateV3StructureToV4(JsonObject root)
    {
        // Snapshot then detach the legacy properties — deserializing into
        // SoundConfig would otherwise complain about extra fields.
        JsonNode? grid = null;
        JsonNode? sounds = null;
        if (root.ContainsKey("grid"))
        {
            grid = root["grid"];
            // .DeepClone gives us an unparented node we can re-attach below.
            grid = grid?.DeepClone();
            root.Remove("grid");
        }
        if (root.ContainsKey("sounds"))
        {
            sounds = root["sounds"];
            sounds = sounds?.DeepClone();
            root.Remove("sounds");
        }

        var defaultBoard = new JsonObject
        {
            ["id"] = "default",
            ["name"] = "Padrão",
            ["color"] = "#3b82f6",
        };
        if (grid is not null) defaultBoard["grid"] = grid;
        if (sounds is not null) defaultBoard["sounds"] = sounds;

        root["boards"] = new JsonArray(defaultBoard);
        root["activeBoardId"] = "default";
    }

    /// <summary>
    /// One-shot migration of v1 configs (device fields = FriendlyName) to v2
    /// (device fields = WASAPI endpoint id). For each populated device field
    /// we try to resolve the legacy FriendlyName to a current device id; if
    /// the device is still present, we keep it under its stable id. If the
    /// device is gone (renamed/unplugged at the time of upgrade) the field
    /// is set to null and the user re-picks it from the UI.
    ///
    /// Idempotent: schemaVersion is bumped to the current version and a no-op
    /// on next launch. Silent: only the schema bump (and any id translation)
    /// is written; no user-facing message. Safe to call after <see cref="Load"/>.
    /// </summary>
    public void MigrateDeviceIdentifiers(DeviceLocator locator)
    {
        bool changed = false;
        lock (_lock)
        {
            // Tracks whether the on-disk config was structurally a v1 (where
            // device fields are FriendlyNames). The pre-parse migration in
            // Load() always bumps SchemaVersion forward, so we can't read it
            // here to detect v1 — but we DID record the original version on
            // the in-memory _config via the schemaVersionBeforeLoadBump field
            // ... actually we don't, so use a heuristic: if any device field
            // is set and DOES NOT match a known endpoint id (via locator),
            // treat it as a legacy FriendlyName and try to translate. This
            // preserves the original "schema v2+ is a no-op" guarantee for
            // any config whose device fields already round-trip as ids.
            bool anyLegacyName = AnyDeviceFieldLooksLikeLegacyName(locator);
            if (!anyLegacyName) return;

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
                _config = migrated;
                changed = true;
            }
            SaveLocked();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Heuristic for detecting whether the in-memory device fields still hold
    /// legacy FriendlyNames (v1 config) rather than endpoint ids (v2+). A
    /// FriendlyName looks human-readable; an endpoint id looks like
    /// "{0.0.0.x}.{guid}". We check via the locator: a value that's neither
    /// empty NOR a known device id NOR resolvable to one is suspect — but we
    /// only translate when at least one field's name resolves to a real id
    /// (so an unknown id on a headless test machine stays preserved instead
    /// of getting silently dropped).
    /// </summary>
    private bool AnyDeviceFieldLooksLikeLegacyName(DeviceLocator locator)
    {
        bool LooksLikeName(string? v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            // Endpoint ids start with '{' — anything else is structurally a
            // FriendlyName (the legacy v1 shape).
            return !v.StartsWith('{');
        }
        return LooksLikeName(_config.AudioDevice)
            || LooksLikeName(_config.MonitorDevice)
            || LooksLikeName(_config.MicDevice);
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

                var active = ResolveActiveBoardLocked(_config);
                var id = DeriveIdFromFilename(finalName, active.Sounds);
                var grid = EnsureCellAvailable(active.Grid, active.Sounds);
                var pos = GridPositioner.NextFree(grid, active.Sounds);
                var entry = new SoundEntry { Id = id, File = finalName, Label = id, Position = pos };
                var updatedBoard = active with
                {
                    Grid = grid,
                    Sounds = new List<SoundEntry>(active.Sounds) { entry },
                };
                _config = ReplaceBoard(_config, updatedBoard);
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

    /// <summary>
    /// Import every audio file in the sounds/ folder that isn't yet referenced
    /// by any board into the active board.
    ///
    /// <para><b>Use only for first-run seeding.</b> Do NOT call this on every
    /// boot or on folder changes: removing a sound from a board intentionally
    /// keeps the file on disk (so it can be re-added later), and a blanket
    /// AutoScan would re-import those "orphan" files on the next launch/upload —
    /// exactly the bug we want to avoid. New sounds are added explicitly via
    /// <see cref="Upload"/>. See <see cref="SeedFromFolderLocked"/>.</para>
    /// </summary>
    public void AutoScan()
    {
        bool changed;
        lock (_lock) { changed = SeedFromFolderLocked(); }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Folder-scan body. Assumes the lock is held and does NOT fire Changed.
    /// Returns true if it added anything. Called once at first-run from Load().
    /// </summary>
    private bool SeedFromFolderLocked()
    {
        if (!Directory.Exists(_opts.SoundsDir)) return false;
        var active = ResolveActiveBoardLocked(_config);
        var allKnown = new HashSet<string>(
            _config.Boards.SelectMany(b => b.Sounds).Select(s => s.File),
            StringComparer.OrdinalIgnoreCase);
        var newEntries = new List<SoundEntry>();
        var grid = active.Grid;
        var working = new List<SoundEntry>(active.Sounds);
        foreach (var file in Directory.EnumerateFiles(_opts.SoundsDir).OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            if (allKnown.Contains(name)) continue;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext)) continue;
            var id = DeriveIdFromFilename(name, working);
            grid = EnsureCellAvailable(grid, working);
            var pos = GridPositioner.NextFree(grid, working);
            var entry = new SoundEntry { Id = id, File = name, Label = id, Position = pos };
            working.Add(entry);
            newEntries.Add(entry);
        }
        if (newEntries.Count == 0 && grid == active.Grid) return false;
        var updatedBoard = active with { Grid = grid, Sounds = working };
        _config = ReplaceBoard(_config, updatedBoard);
        SaveLocked();
        return true;
    }

    public void RepairInvariants()
    {
        bool changed = false;
        lock (_lock)
        {
            // Repair every board independently — each owns its own grid +
            // positions so a duplicate in board A doesn't bleed into board B.
            var boards = new List<Board>(_config.Boards.Count);
            bool anyChanged = false;
            foreach (var b in _config.Boards)
            {
                var repaired = GridPositioner.RepairDuplicates(b.Grid, b.Sounds);
                if (repaired.SequenceEqual(b.Sounds))
                {
                    boards.Add(b);
                }
                else
                {
                    boards.Add(b with { Sounds = repaired });
                    anyChanged = true;
                }
            }
            if (!anyChanged) return;
            _config = _config with { Boards = boards };
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
            // Runtime status is per-sound (file present on disk?), so we
            // surface every sound across every board. Sound ids are unique
            // per board but the StateDto only ever projects the active
            // board's sounds, so phantom collisions don't matter here.
            return _config.Boards
                .SelectMany(b => b.Sounds)
                .Select(s => new SoundRuntimeStatus(s.Id, !File.Exists(Path.Combine(_opts.SoundsDir, s.File))))
                .ToList();
        }
    }

    public void UpdateSound(string id, string? label = null, string? color = null,
                            string? icon = null, GridPosition? position = null,
                            bool clearPosition = false, int? volume = null)
    {
        lock (_lock)
        {
            var active = ResolveActiveBoardLocked(_config);
            var idx = active.Sounds.ToList().FindIndex(s => s.Id == id);
            if (idx < 0) throw new InvalidOperationException($"Unknown sound id: {id}");

            // If a position move is requested, do the swap on the ORIGINAL list so
            // GridPositioner.Swap can read the moving entry's pre-move position to
            // hand it to the displaced occupant. Then apply field updates on top.
            List<SoundEntry> sounds;
            if (position is not null && !clearPosition)
            {
                sounds = GridPositioner.Swap(active.Sounds, id, position);
            }
            else
            {
                sounds = active.Sounds.ToList();
            }

            var i = sounds.FindIndex(s => s.Id == id);
            var current = sounds[i];
            sounds[i] = current with
            {
                Label = label ?? current.Label,
                Color = color ?? current.Color,
                Icon = icon ?? current.Icon,
                Position = clearPosition ? null : current.Position,
                Volume = volume is not null ? Math.Clamp(volume.Value, 0, 100) : current.Volume,
            };
            _config = ReplaceBoard(_config, active with { Sounds = sounds });
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void DeleteSound(string id, bool deleteFile)
    {
        lock (_lock)
        {
            var active = ResolveActiveBoardLocked(_config);
            var entry = active.Sounds.FirstOrDefault(s => s.Id == id)
                ?? throw new InvalidOperationException($"Unknown sound id: {id}");
            // Only delete the file from disk if no OTHER board references it.
            // Without this guard, deleting a sound from board A would silently
            // break a copy of the same file living in board B.
            if (deleteFile)
            {
                bool referencedElsewhere = _config.Boards
                    .Where(b => b.Id != active.Id)
                    .SelectMany(b => b.Sounds)
                    .Any(s => string.Equals(s.File, entry.File, StringComparison.OrdinalIgnoreCase));
                if (!referencedElsewhere)
                {
                    var path = Path.Combine(_opts.SoundsDir, entry.File);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            var updatedBoard = active with
            {
                Sounds = active.Sounds.Where(s => s.Id != id).ToList(),
            };
            _config = ReplaceBoard(_config, updatedBoard);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void ResizeGrid(int cols, int rows)
    {
        lock (_lock)
        {
            var active = ResolveActiveBoardLocked(_config);
            var newGrid = new GridLayout(cols, rows);
            var updated = active.Sounds.Select(s =>
                s.Position is not null && !newGrid.Contains(s.Position)
                    ? s with { Position = null } : s).ToList();
            _config = ReplaceBoard(_config, active with { Grid = newGrid, Sounds = updated });
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
            // Scan every board — the engine plays whatever id was requested,
            // even if the user switched boards mid-play. Stats stay attached
            // to the original entry.
            for (int bi = 0; bi < _config.Boards.Count; bi++)
            {
                var board = _config.Boards[bi];
                var sounds = board.Sounds.ToList();
                var idx = sounds.FindIndex(s => s.Id == soundId);
                if (idx < 0) continue;
                sounds[idx] = sounds[idx] with
                {
                    PlayCount = sounds[idx].PlayCount + 1,
                    LastPlayedAt = DateTime.UtcNow,
                };
                _config = ReplaceBoard(_config, board with { Sounds = sounds });
                SaveLocked();
                changed = true;
                break;
            }
        }
        if (changed) Changed?.Invoke();
    }

    public void ApplyLayout(IEnumerable<(string Id, GridPosition? Position)> placements)
    {
        lock (_lock)
        {
            var active = ResolveActiveBoardLocked(_config);
            var list = placements.ToList();
            var nonNull = list.Where(p => p.Position is not null).ToList();
            if (nonNull.Select(p => p.Position).Distinct().Count() != nonNull.Count)
                throw new InvalidOperationException("Duplicate positions in layout");
            foreach (var (entryId, pos) in list)
                if (pos is not null && !active.Grid.Contains(pos))
                    throw new InvalidOperationException($"Position {pos} out of grid {active.Grid}");
            var map = list.ToDictionary(p => p.Id, p => p.Position);
            var updated = active.Sounds
                .Select(s => map.TryGetValue(s.Id, out var p) ? s with { Position = p } : s)
                .ToList();
            _config = ReplaceBoard(_config, active with { Sounds = updated });
            SaveLocked();
        }
        Changed?.Invoke();
    }

    // ─── board management ────────────────────────────────────────────────

    /// <summary>
    /// Create a new board and append it to the config. The id is slugified
    /// from <paramref name="name"/> and disambiguated against existing board
    /// ids (so two "Stream" boards become "stream" and "stream-2"). The
    /// new board is NOT activated automatically — callers explicitly activate
    /// via <see cref="ActivateBoard"/> to keep the two operations distinct.
    /// </summary>
    public Board CreateBoard(string name, string? color = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Board name cannot be empty");
        Board created;
        lock (_lock)
        {
            var id = SlugifyBoardId(name, _config.Boards.Select(b => b.Id));
            created = new Board { Id = id, Name = name.Trim(), Color = color ?? "#3b82f6" };
            _config = _config with
            {
                Boards = new List<Board>(_config.Boards) { created },
            };
            SaveLocked();
        }
        Changed?.Invoke();
        return created;
    }

    /// <summary>
    /// Rename and/or recolor an existing board. Pass null to keep the current
    /// value of either field. Throws if the board id is unknown.
    /// </summary>
    public void UpdateBoard(string boardId, string? name = null, string? color = null)
    {
        lock (_lock)
        {
            var idx = _config.Boards.FindIndex(b => b.Id == boardId);
            if (idx < 0) throw new InvalidOperationException($"Unknown board id: {boardId}");
            var b = _config.Boards[idx];
            var updated = b with
            {
                Name = !string.IsNullOrWhiteSpace(name) ? name.Trim() : b.Name,
                Color = !string.IsNullOrWhiteSpace(color) ? color : b.Color,
            };
            var newBoards = new List<Board>(_config.Boards);
            newBoards[idx] = updated;
            _config = _config with { Boards = newBoards };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Delete a board. If the deleted board was the active one, activates
    /// another board automatically. Refuses to delete the last remaining
    /// board (the UI assumes at least one board exists). Files referenced
    /// only by the deleted board are NOT removed from disk — the user can
    /// clear them manually from the sounds/ folder, or re-import them into
    /// a different board via AutoScan.
    /// </summary>
    public void DeleteBoard(string boardId)
    {
        lock (_lock)
        {
            if (_config.Boards.Count <= 1)
                throw new InvalidOperationException("Cannot delete the last board");
            var idx = _config.Boards.FindIndex(b => b.Id == boardId);
            if (idx < 0) throw new InvalidOperationException($"Unknown board id: {boardId}");
            var newBoards = _config.Boards.Where(b => b.Id != boardId).ToList();
            var newActive = _config.ActiveBoardId == boardId
                ? newBoards[0].Id
                : _config.ActiveBoardId;
            _config = _config with { Boards = newBoards, ActiveBoardId = newActive };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Switch the active board. No-op if already active.</summary>
    public void ActivateBoard(string boardId)
    {
        bool changed = false;
        lock (_lock)
        {
            if (!_config.Boards.Any(b => b.Id == boardId))
                throw new InvalidOperationException($"Unknown board id: {boardId}");
            if (_config.ActiveBoardId == boardId) return;
            _config = _config with { ActiveBoardId = boardId };
            SaveLocked();
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Move a sound from the active board to <paramref name="targetBoardId"/>.
    /// The sound keeps its id, label, color, volume, and usage stats but loses
    /// its grid position (the target board's grid layout is independent — the
    /// caller can re-place explicitly via <see cref="UpdateSound"/>).
    /// </summary>
    public void MoveSoundToBoard(string soundId, string targetBoardId)
    {
        lock (_lock)
        {
            if (_config.ActiveBoardId == targetBoardId) return; // no-op
            var sourceIdx = _config.Boards.FindIndex(b => b.Id == _config.ActiveBoardId);
            var targetIdx = _config.Boards.FindIndex(b => b.Id == targetBoardId);
            if (targetIdx < 0) throw new InvalidOperationException($"Unknown target board: {targetBoardId}");

            var source = _config.Boards[sourceIdx];
            var entry = source.Sounds.FirstOrDefault(s => s.Id == soundId)
                ?? throw new InvalidOperationException($"Unknown sound id: {soundId}");

            // Disambiguate id collision against the target board — the same
            // file could already have been imported there via AutoScan.
            var target = _config.Boards[targetIdx];
            var newId = entry.Id;
            var existingTargetIds = new HashSet<string>(target.Sounds.Select(s => s.Id));
            if (existingTargetIds.Contains(newId))
            {
                int n = 2;
                while (existingTargetIds.Contains($"{entry.Id}-{n}")) n++;
                newId = $"{entry.Id}-{n}";
            }

            // Find an open cell on the target grid; expand if full.
            var targetGrid = EnsureCellAvailable(target.Grid, target.Sounds);
            var newPos = GridPositioner.NextFree(targetGrid, target.Sounds);

            var moved = entry with { Id = newId, Position = newPos };
            var newSource = source with { Sounds = source.Sounds.Where(s => s.Id != soundId).ToList() };
            var newTarget = target with { Grid = targetGrid, Sounds = new List<SoundEntry>(target.Sounds) { moved } };
            var boards = new List<Board>(_config.Boards);
            boards[sourceIdx] = newSource;
            boards[targetIdx] = newTarget;
            _config = _config with { Boards = boards };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    // ─── helpers ────────────────────────────────────────────────────────

    private static Board ResolveActiveBoardLocked(SoundConfig cfg)
    {
        var b = cfg.Boards.FirstOrDefault(x => x.Id == cfg.ActiveBoardId);
        if (b is not null) return b;
        // Defensive: ActiveBoardId pointed nowhere. Fall back to the first
        // board rather than throwing — Load() should have caught this but
        // we don't want any code path to fail on an inconsistent config.
        if (cfg.Boards.Count > 0) return cfg.Boards[0];
        throw new InvalidOperationException("No boards configured");
    }

    private static SoundConfig ReplaceBoard(SoundConfig cfg, Board updated)
    {
        var idx = cfg.Boards.FindIndex(b => b.Id == updated.Id);
        if (idx < 0) throw new InvalidOperationException($"Unknown board id: {updated.Id}");
        var newBoards = new List<Board>(cfg.Boards);
        newBoards[idx] = updated;
        return cfg with { Boards = newBoards };
    }

    private static string SlugifyBoardId(string name, IEnumerable<string> existingIds)
    {
        var lower = name.Trim().ToLowerInvariant();
        var slug = System.Text.RegularExpressions.Regex.Replace(lower, @"[^a-z0-9_]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "board";
        var existing = new HashSet<string>(existingIds);
        if (!existing.Contains(slug)) return slug;
        for (int n = 2; n < 1000; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!existing.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Cannot derive unique board id");
    }
}
