# M1 / F1 — Volume Normalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** All sounds play at a consistent perceived loudness, equalized toward the user's voice level, with a global on/off toggle — so a quiet meme and a hot mp3 don't differ wildly in the same board.

**Architecture:** A pure managed loudness analyzer computes a per-sound gain (in dB) from the already-decoded PCM the first time a sound plays; the gain is persisted on the `SoundEntry` (schema v5). The `PlaybackEngine` folds that gain into the effective volume `(global × per-sound × normalize)`, and the existing limiter (T6) catches any overshoot. A global `NormalizeEnabled` toggle gates the whole thing.

**Tech Stack:** .NET 8 (`Soundpad.*` namespace, brand "Reson"), NAudio, xUnit + Moq + FluentAssertions. UI: WPF (code-behind), vanilla web (wwwroot), Flutter (mobile).

**Spec:** `docs/superpowers/specs/2026-05-23-reson-roadmap-design.md` → Milestone 1, gap F1.

**Scope note:** This is the F1 slice of Milestone 1 (which also has T6, T4, T9, T7a, U5, T1 — each gets its own plan). F1 is foundational because it defines schema v5. Build backend first (Tasks 1-5), then the UI toggle (Tasks 6-8).

**Sequence F1 AFTER T6 within M1.** Normalization boosts quiet clips up to +12 dB, which pushes peaks closer to (or past) 0 dBFS on peaky-but-quiet-RMS material. Today's limiter is a **hard clamp** — boosting into it distorts. T6 (limiter soft-knee, separate M1 plan) is small and schema-free; doing it first means F1's boosts hit a graceful knee instead of a hard ceiling. If F1 ships before T6, accept temporary added distortion on a minority of clips. (Overflow is never a risk either way — the hard clamp prevents that; the issue is *quality*, which is exactly what T6 fixes.) Recommended M1 order: T6 → F1 → (T4, T9, T7a, U5, T1).

---

## Design decisions (locked)

- **Loudness metric:** RMS-based (not peak). Peak normalization leaves a quiet-but-peaky clip quiet; RMS approximates *perceived* loudness, which is what "equalize to voice level" needs. True LUFS is overkill and not pure-managed; RMS is a good-enough, dependency-free approximation.
- **Target:** −20 dBFS RMS (a reasonable speech-level target; configurable constant). The limiter (T6) protects against the rare clip where the boost overshoots.
- **Max boost cap:** +12 dB. Prevents amplifying near-silent clips into noise.
- **When computed:** lazily, on first play (the PCM is already decoded in `SoundCache` at that moment). Computed value persists, so subsequent plays are instant. `NormalizeGainDb` is nullable — `null` = not yet computed.
- **Applied:** `effective = (global/100) × (perSound/100) × dbToLinear(NormalizeGainDb)` when `NormalizeEnabled`; otherwise the normalize factor is `1.0`.
- **Schema:** v4 → v5. Both new fields are additive (nullable / default), so the existing deserializer+stamp migration handles it — no data transform needed.
- **Retroactive default (conscious decision):** `NormalizeEnabled` defaults to `true`, so on the v1.1 auto-update **every existing board starts normalizing without the user asking** — sounds will audibly change level on first boot. This is intentional ("equalize out of the box"), but it's a silent behavior change shipped via auto-update. **Must be called out in the v1.1 changelog/release notes** ("Volume normalization is now on by default — toggle it off in settings if you preferred the raw levels"). Don't let it be a surprise the user discovers via T9 crash/feedback reports.

---

## Task 1: Schema v5 — model fields + migration

**Files:**
- Modify: `src/Soundpad/Models/SoundEntry.cs`
- Modify: `src/Soundpad/Models/SoundConfig.cs`
- Modify: `src/Soundpad/Sound/SoundLibrary.cs` (CurrentSchemaVersion 4 → 5)
- Test: `tests/Soundpad.Tests/Sound/SoundLibraryV5MigrationTests.cs` (new)

- [ ] **Step 1: Write the failing migration test**

Create `tests/Soundpad.Tests/Sound/SoundLibraryV5MigrationTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryV5MigrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "reson-v5-" + Guid.NewGuid());
    public SoundLibraryV5MigrationTests() { Directory.CreateDirectory(Path.Combine(_tempDir, "sounds")); }
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public void V4_Config_Upgrades_To_V5_With_Defaults()
    {
        // A v4 config: boards with a sound that has no normalize field + no NormalizeEnabled.
        var v4 = @"{
            ""schemaVersion"": 4,
            ""authToken"": ""abc"",
            ""boards"": [{ ""id"": ""default"", ""name"": ""Padrão"", ""color"": ""#3b82f6"",
                ""grid"": { ""cols"": 3, ""rows"": 4 },
                ""sounds"": [{ ""id"": ""s1"", ""file"": ""s1.mp3"", ""label"": ""S1"",
                    ""position"": { ""col"": 0, ""row"": 0 } }] }],
            ""activeBoardId"": ""default""
        }";
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), v4);

        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();

        lib.Config.SchemaVersion.Should().Be(5);
        lib.Config.NormalizeEnabled.Should().BeTrue();           // new global default
        lib.Config.Boards[0].Sounds[0].NormalizeGainDb.Should().BeNull();  // not computed yet
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --filter SoundLibraryV5MigrationTests`
Expected: FAIL — `NormalizeEnabled` / `NormalizeGainDb` don't exist, schema is 4.

- [ ] **Step 3: Add the model fields**

In `src/Soundpad/Models/SoundEntry.cs`, after the `Volume` property (line 21):

```csharp
    // Schema v5: per-sound loudness-normalization gain in dB, computed lazily
    // from the decoded PCM on first play (RMS-based, toward NormalizeTargetDb).
    // null = not yet computed. Applied multiplicatively with Volume + global
    // volume in the engine when SoundConfig.NormalizeEnabled is true.
    public double? NormalizeGainDb { get; init; } = null;
```

In `src/Soundpad/Models/SoundConfig.cs`, add alongside the other global fields (e.g., near `Volume`/`MonitorEnabled`):

```csharp
    // Schema v5: global toggle for loudness normalization. Default on so
    // sounds equalize out of the box; user can disable to hear raw levels.
    public bool NormalizeEnabled { get; init; } = true;
```

Bump the default in `SoundConfig`: find `public int SchemaVersion { get; init; } = 4;` → change to `= 5;`.

In `src/Soundpad/Sound/SoundLibrary.cs`, find `private const int CurrentSchemaVersion = 4;` → change to `= 5;`.

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test --filter SoundLibraryV5MigrationTests`
Expected: PASS. (The existing `Load` migration stamps `SchemaVersion` forward to `CurrentSchemaVersion`; the new fields default via the deserializer.)

- [ ] **Step 5: Run the existing migration suite for regressions**

Run: `dotnet test --filter "FullyQualifiedName~Migration|FullyQualifiedName~SoundConfigTests"`
Expected: existing tests now expecting schema 4 may fail — update any assertion that hardcodes `SchemaVersion.Should().Be(4)` to `Be(5)`. Search: `grep -rn "Be(4)" tests/`. Fix each to `Be(5)`.

- [ ] **Step 6: Commit**

```bash
git add src/Soundpad/Models/SoundEntry.cs src/Soundpad/Models/SoundConfig.cs src/Soundpad/Sound/SoundLibrary.cs tests/Soundpad.Tests/
git commit -m "feat(library): schema v5 — NormalizeGainDb + NormalizeEnabled"
```

---

## Task 2: LoudnessAnalyzer (pure RMS gain)

**Files:**
- Create: `src/Soundpad/Audio/LoudnessAnalyzer.cs`
- Test: `tests/Soundpad.Tests/Audio/LoudnessAnalyzerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Soundpad.Tests/Audio/LoudnessAnalyzerTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class LoudnessAnalyzerTests
{
    // Build interleaved-stereo IEEE-float PCM bytes from a constant amplitude.
    private static byte[] ConstantPcm(float amplitude, int frames = 4800, int channels = 2)
    {
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++) samples[i] = amplitude;
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Gain_Boosts_Quiet_Clip_Toward_Target()
    {
        // amplitude 0.01 → RMS ≈ -40 dBFS. Target -20 → wants +20 dB, capped to +12.
        var db = LoudnessAnalyzer.ComputeGainDb(ConstantPcm(0.01f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().BeApproximately(12.0, 0.5); // capped
    }

    [Fact]
    public void Gain_Attenuates_Loud_Clip_Toward_Target()
    {
        // amplitude 0.5 → RMS ≈ -6 dBFS. Target -20 → wants about -14 dB.
        var db = LoudnessAnalyzer.ComputeGainDb(ConstantPcm(0.5f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().BeApproximately(-14.0, 0.5);
    }

    [Fact]
    public void Silence_Returns_Zero_Gain_Not_Infinite()
    {
        var db = LoudnessAnalyzer.ComputeGainDb(ConstantPcm(0f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().Be(0.0); // never amplify silence
    }

    [Fact]
    public void Empty_Pcm_Returns_Zero_Gain()
    {
        LoudnessAnalyzer.ComputeGainDb(Array.Empty<byte>(), 2, -20, 12).Should().Be(0.0);
    }

    // The constant-amplitude tests above use DC, where RMS == amplitude. Real
    // audio is AC: a sine of peak A has RMS = A/√2 (≈ -3 dB below its peak).
    // This case exercises the actual RMS path so a peak-vs-RMS mixup can't slip
    // through. Peak 0.5 sine → RMS ≈ 0.3536 → -9.03 dBFS → target -20 wants ≈ -11 dB.
    private static byte[] SinePcm(float peak, int frames = 48000, int channels = 2, double freq = 440, int rate = 48000)
    {
        var samples = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)(peak * Math.Sin(2 * Math.PI * freq * f / rate));
            for (int c = 0; c < channels; c++) samples[f * channels + c] = v;
        }
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Gain_Uses_Rms_Not_Peak_For_Sine()
    {
        // If the analyzer wrongly used peak (0.5 → -6 dBFS) it would compute -14;
        // correct RMS (0.3536 → -9.03 dBFS) computes ≈ -10.97.
        var db = LoudnessAnalyzer.ComputeGainDb(SinePcm(0.5f), channels: 2, targetDb: -20, maxBoostDb: 12);
        db.Should().BeApproximately(-10.97, 0.5);
    }
}
```

- [ ] **Step 2: Run, verify fails**

Run: `dotnet test --filter LoudnessAnalyzerTests`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Implement LoudnessAnalyzer**

Create `src/Soundpad/Audio/LoudnessAnalyzer.cs`:

```csharp
namespace Soundpad.Audio;

/// <summary>
/// Pure, dependency-free loudness analysis. Computes an RMS-based gain (dB) to
/// bring a clip toward a target level — a cheap approximation of perceived
/// loudness (true LUFS is overkill here). Input is interleaved IEEE-float PCM
/// bytes (the format Reson caches decoded sounds in).
/// </summary>
public static class LoudnessAnalyzer
{
    /// <summary>
    /// Returns the gain in dB to apply so the clip's RMS reaches targetDb,
    /// clamped to [-60, maxBoostDb]. Silence / empty input returns 0 (no change),
    /// never +infinity.
    /// </summary>
    public static double ComputeGainDb(byte[] pcmFloatBytes, int channels, double targetDb, double maxBoostDb)
    {
        if (pcmFloatBytes.Length < 4) return 0.0;
        int sampleCount = pcmFloatBytes.Length / 4;
        double sumSquares = 0;
        var span = pcmFloatBytes.AsSpan();
        for (int i = 0; i < sampleCount; i++)
        {
            float s = BitConverter.ToSingle(span.Slice(i * 4, 4));
            sumSquares += (double)s * s;
        }
        double rms = Math.Sqrt(sumSquares / sampleCount);
        if (rms <= 1e-6) return 0.0; // effectively silence — don't amplify

        double rmsDb = 20.0 * Math.Log10(rms);
        double gain = targetDb - rmsDb;
        return Math.Clamp(gain, -60.0, maxBoostDb);
    }

    /// <summary>Convert a dB gain to a linear multiplier.</summary>
    public static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);
}
```

- [ ] **Step 4: Run, verify passes**

Run: `dotnet test --filter LoudnessAnalyzerTests`
Expected: 5 passing (incl. the sine/RMS case).

- [ ] **Step 5: Commit**

```bash
git add src/Soundpad/Audio/LoudnessAnalyzer.cs tests/Soundpad.Tests/Audio/LoudnessAnalyzerTests.cs
git commit -m "feat(audio): RMS loudness analyzer (gain toward target dB)"
```

---

## Task 3: SoundLibrary.SetNormalizeGainDb + SetNormalizeEnabled

**Files:**
- Modify: `src/Soundpad/Sound/SoundLibrary.cs`
- Test: `tests/Soundpad.Tests/Sound/SoundLibraryNormalizeTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Soundpad.Tests/Sound/SoundLibraryNormalizeTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryNormalizeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "reson-norm-" + Guid.NewGuid());
    private readonly SoundLibrary _lib;

    public SoundLibraryNormalizeTests()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "a.mp3"), new byte[10]);
        _lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        _lib.Load();
        _lib.Upload("a.mp3", new MemoryStream(new byte[10])); // one entry on active board
    }
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public void SetNormalizeGainDb_Persists_And_Raises_Changed()
    {
        var id = _lib.ActiveBoard.Sounds[0].Id;
        var raised = 0; _lib.Changed += () => Interlocked.Increment(ref raised);
        _lib.SetNormalizeGainDb(id, -3.5);
        _lib.ActiveBoard.Sounds[0].NormalizeGainDb.Should().Be(-3.5);
        raised.Should().BeGreaterThan(0);

        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.ActiveBoard.Sounds[0].NormalizeGainDb.Should().Be(-3.5);
    }

    [Fact]
    public void SetNormalizeGainDb_Unknown_Id_Is_NoOp()
    {
        _lib.Invoking(l => l.SetNormalizeGainDb("nope", -3.0)).Should().NotThrow();
    }

    [Fact]
    public void SetNormalizeEnabled_Persists()
    {
        _lib.SetNormalizeEnabled(false);
        _lib.Config.NormalizeEnabled.Should().BeFalse();
        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.NormalizeEnabled.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run, verify fails**

Run: `dotnet test --filter SoundLibraryNormalizeTests`
Expected: FAIL — methods don't exist.

- [ ] **Step 3: Implement the two methods**

Append to the `SoundLibrary` class in `src/Soundpad/Sound/SoundLibrary.cs` (follow the existing `UpdateSound` pattern — find a sound across boards, replace it, `SaveLocked`, raise `Changed`):

```csharp
    /// <summary>
    /// Store the computed normalization gain for a sound (idempotent). No-op if
    /// the id isn't found on any board. Persists + raises Changed when it changes.
    /// </summary>
    public void SetNormalizeGainDb(string id, double gainDb)
    {
        bool changed = false;
        lock (_lock)
        {
            var boards = new List<Board>(_config.Boards.Count);
            foreach (var b in _config.Boards)
            {
                var idx = b.Sounds.FindIndex(s => s.Id == id);
                if (idx < 0) { boards.Add(b); continue; }
                if (b.Sounds[idx].NormalizeGainDb == gainDb) { boards.Add(b); continue; }
                var sounds = new List<SoundEntry>(b.Sounds);
                sounds[idx] = sounds[idx] with { NormalizeGainDb = gainDb };
                boards.Add(b with { Sounds = sounds });
                changed = true;
            }
            if (!changed) return;
            _config = _config with { Boards = boards };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Toggle global loudness normalization. Persists + raises Changed.</summary>
    public void SetNormalizeEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_config.NormalizeEnabled == enabled) return;
            _config = _config with { NormalizeEnabled = enabled };
            SaveLocked();
        }
        Changed?.Invoke();
    }
```

- [ ] **Step 4: Run, verify passes**

Run: `dotnet test --filter SoundLibraryNormalizeTests`
Expected: 3 passing.

- [ ] **Step 5: Commit**

```bash
git add src/Soundpad/Sound/SoundLibrary.cs tests/Soundpad.Tests/Sound/SoundLibraryNormalizeTests.cs
git commit -m "feat(library): SetNormalizeGainDb + SetNormalizeEnabled"
```

---

## Task 4: PlaybackEngine — compute + apply normalization

**Files:**
- Modify: `src/Soundpad/Audio/PlaybackEngine.cs`
- Test: `tests/Soundpad.Tests/Audio/NormalizationPlaybackTests.cs`

**Context:** In `HandlePlay` (around line 405-410) the engine currently does:
```csharp
_activePerSoundFactor = LookupPerSoundFactor(cmd.SoundId);
var effective = (_volume / 100f) * _activePerSoundFactor;
var gameSound = new SoundSampleProvider(cached, WorkingFormat, effective);
```
We fold a normalization multiplier into `effective`. The engine already holds a `SoundLibrary` (`_library`, added for `RecordPlay`). Two correctness requirements that this task bakes in from the start (not as an afterthought):

1. **Persist OFF the audio thread.** Computing the gain (one RMS pass over the in-memory float array) is fast and stays on the audio thread. But `SetNormalizeGainDb` → `SaveLocked` does *synchronous disk I/O*; running it on the audio thread on the first play of each sound risks an audible glitch right when the user is listening. So: apply the just-computed gain immediately (we have the value in hand), and **enqueue the persist on a background task**. `SetNormalizeGainDb` is idempotent and lock-guarded, so a backgrounded write is safe; if a second play races before the write lands, it recomputes the same value — harmless.
2. **Mid-play volume keeps normalization.** `HandleVolume` recomputes `effective` when the master fader moves during playback. It must fold the *same* normalize multiplier, or dragging the volume mid-play would drop the normalization. We store the active multiplier in a field `_activeNormalizeLinear` set in `HandlePlay` and reused in `HandleVolume`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Soundpad.Tests/Audio/NormalizationPlaybackTests.cs`. Drives the engine with a fake player + real `SoundLibrary`. **Waits on the observed outcome (`WaitUntil`), not a fixed `Task.Delay`** — a fixed delay is flaky in CI under load. (Note: there is no `IdleAsync` helper on the engine; poll the real result instead.)

```csharp
using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;
using Soundpad.Sound;

namespace Soundpad.Tests.Audio;

public class NormalizationPlaybackTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "reson-normpb-" + Guid.NewGuid());
    public NormalizationPlaybackTests() { Directory.CreateDirectory(Path.Combine(_tempDir, "sounds")); }
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("condition not met in time");
    }

    private static CachedSound MakeCached(float amplitude)
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var samples = new float[48000 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = amplitude;
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return new CachedSound(fmt, bytes);
    }

    private (PlaybackEngine engine, SoundLibrary lib, List<FakeWavePlayer> players) Build(float amplitude, bool normalize = true)
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        if (!normalize) lib.SetNormalizeEnabled(false);
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>())).Returns(MakeCached(amplitude));
        var cache = new SoundCache(decoder.Object, 50);
        var players = new List<FakeWavePlayer>();
        var factory = new Mock<IWavePlayerFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string n, int l) => { var p = new FakeWavePlayer(n); players.Add(p); return p; });
        var engine = new PlaybackEngine(factory.Object, cache, lib); // 3-arg ctor (mic null)
        engine.SetGameDevice("game");
        engine.SetVolume(100);
        engine.Start();
        return (engine, lib, players);
    }

    [Fact]
    public async Task First_Play_Computes_And_Persists_NormalizeGain()
    {
        var (engine, lib, _) = Build(0.01f);          // 0.01 DC → -40 dBFS → wants +20, capped at +12
        var up = lib.Upload("a.mp3", new MemoryStream(new byte[10]));
        engine.Play(up.Entry.Id, Path.Combine(_tempDir, "sounds", up.FinalFilename));

        // Poll the persisted outcome — the persist is backgrounded, so wait for it.
        await WaitUntil(() => lib.ActiveBoard.Sounds.Single(s => s.Id == up.Entry.Id).NormalizeGainDb is not null);
        lib.ActiveBoard.Sounds.Single(s => s.Id == up.Entry.Id).NormalizeGainDb.Should().BeApproximately(12.0, 0.5);
        engine.Shutdown();
    }

    [Fact]
    public async Task Normalize_Disabled_Does_Not_Compute_Gain()
    {
        var (engine, lib, _) = Build(0.01f, normalize: false);
        var up = lib.Upload("b.mp3", new MemoryStream(new byte[10]));
        engine.Play(up.Entry.Id, Path.Combine(_tempDir, "sounds", up.FinalFilename));
        await Task.Delay(200); // brief settle — asserting a NON-event
        lib.ActiveBoard.Sounds.Single(s => s.Id == up.Entry.Id).NormalizeGainDb.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Volume_Change_Mid_Play_Preserves_Normalization()
    {
        var (engine, lib, players) = Build(0.01f);
        var up = lib.Upload("c.mp3", new MemoryStream(new byte[10]));
        engine.Play(up.Entry.Id, Path.Combine(_tempDir, "sounds", up.FinalFilename));
        await WaitUntil(() => engine.ActiveGameEffectiveVolumeForTests is not null);

        var beforeNorm = engine.ActiveNormalizeLinearForTests;   // the normalize multiplier in effect
        beforeNorm.Should().BeApproximately(LoudnessAnalyzer.DbToLinear(12.0), 0.01);

        // Move the master fader mid-play; normalization must survive.
        engine.SetVolume(50);
        await WaitUntil(() => Math.Abs(engine.ActiveGameEffectiveVolumeForTests!.Value
                                       - (0.5f * 1f * beforeNorm)) < 0.001f);
        engine.Shutdown();
    }
}
```

This test references two internal test seams on `PlaybackEngine`: `ActiveNormalizeLinearForTests` (the `_activeNormalizeLinear` field) and `ActiveGameEffectiveVolumeForTests` (the active game sound's current `Volume`, or null when idle). Expose them as `internal` and rely on the existing `InternalsVisibleTo("Soundpad.Tests")`.

- [ ] **Step 2: Run, verify fails**

Run: `dotnet test --filter NormalizationPlaybackTests`
Expected: FAIL — fields/seams don't exist; gain stays null.

- [ ] **Step 3: Implement compute + apply (off-thread persist + mid-play field)**

In `src/Soundpad/Audio/PlaybackEngine.cs`, add constants + the field near the other active-playback fields (next to `_activePerSoundFactor`):

```csharp
    private const double NormalizeTargetDb = -20.0;
    private const double NormalizeMaxBoostDb = 12.0;
    private float _activeNormalizeLinear = 1f;

    // Test seams (InternalsVisibleTo: Soundpad.Tests).
    internal float ActiveNormalizeLinearForTests => _activeNormalizeLinear;
    internal float? ActiveGameEffectiveVolumeForTests => _activeGameSound?.Volume;
```

In `HandlePlay`, replace the effective-volume computation with:

```csharp
_activePerSoundFactor = LookupPerSoundFactor(cmd.SoundId);
_activeNormalizeLinear = ComputeNormalizeLinear(cmd.SoundId, cached);
var effective = (_volume / 100f) * _activePerSoundFactor * _activeNormalizeLinear;
var gameSound = new SoundSampleProvider(cached, WorkingFormat, effective);
```

The monitor branch already reuses `effective`, so it inherits normalization automatically.

In `HandleVolume`, fold the normalize multiplier into the mid-play recompute (it currently uses only `_activePerSoundFactor`):

```csharp
var effective = (_volume / 100f) * _activePerSoundFactor * _activeNormalizeLinear;
if (_activeGameSound is not null) _activeGameSound.Volume = effective;
if (_activeMonitorSound is not null) _activeMonitorSound.Volume = effective;
```

Add the helper — note the **off-thread persist**:

```csharp
    // Linear normalization multiplier for a sound. Disabled → 1.0. If the gain
    // isn't stored yet, compute it from the decoded PCM (fast, on this thread)
    // and apply immediately; persist on a background task so the audio thread
    // never blocks on disk I/O (avoids a first-play glitch). SetNormalizeGainDb
    // is idempotent + lock-guarded, so a backgrounded/raced write is safe.
    private float ComputeNormalizeLinear(string soundId, CachedSound cached)
    {
        if (_library is null || !_library.Config.NormalizeEnabled) return 1f;
        var entry = _library.Config.Boards
            .SelectMany(b => b.Sounds)
            .FirstOrDefault(s => s.Id == soundId);
        if (entry is null) return 1f;

        if (entry.NormalizeGainDb is double stored)
            return LoudnessAnalyzer.DbToLinear(stored);

        double gainDb = LoudnessAnalyzer.ComputeGainDb(
            cached.PcmBytes, cached.Format.Channels, NormalizeTargetDb, NormalizeMaxBoostDb);
        _ = Task.Run(() => _library.SetNormalizeGainDb(soundId, gainDb)); // persist off the audio thread
        return LoudnessAnalyzer.DbToLinear(gainDb);
    }
```

- [ ] **Step 4: Run, verify passes**

Run: `dotnet test --filter NormalizationPlaybackTests`
Expected: 3 passing.

- [ ] **Step 5: Run the full audio suite for regressions**

Run: `dotnet test --filter "FullyQualifiedName~Audio"`
Expected: all passing. (The `HandleVolume` fold and the `_activeNormalizeLinear` field were implemented in Step 3, not deferred here — this step only verifies no regression.)

- [ ] **Step 6: Commit**

```bash
git add src/Soundpad/Audio/PlaybackEngine.cs tests/Soundpad.Tests/Audio/NormalizationPlaybackTests.cs
git commit -m "feat(audio): RMS normalization in playback (off-thread persist, mid-play-safe)"
```

---

## Task 5: API — expose toggle + gain

**Files:**
- Modify: `src/Soundpad/Api/Dto/StateDto.cs`
- Modify: `src/Soundpad/Api/PlaybackEndpoints.cs`
- Test: `tests/Soundpad.Tests/Api/NormalizeEndpointTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Soundpad.Tests/Api/NormalizeEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

public class NormalizeEndpointTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public NormalizeEndpointTests(TestingWebApplicationFactory f) { _factory = f; }

    private HttpClient Auth()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task Post_Normalize_Toggles_And_Persists()
    {
        var c = Auth();
        var r = await c.PostAsJsonAsync("/api/normalize", new { enabled = false });
        r.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Services.GetRequiredService<SoundLibrary>().Config.NormalizeEnabled.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run, verify fails**

Run: `dotnet test --filter NormalizeEndpointTests`
Expected: FAIL — 404 (endpoint missing).

- [ ] **Step 3: Add the DTO fields + endpoint**

In `src/Soundpad/Api/Dto/StateDto.cs`:
- Add `double? NormalizeGainDb = null` to the `SoundEntryDto` record (after `Volume`).
- Add `bool NormalizeEnabled = true` to the `StateDto` record (after `MonitorEnabled` or at the end with the other optionals).

Wherever `StateDto` / `SoundEntryDto` are built (the `/api/state` handler in `PlaybackEndpoints.cs`), pass `lib.Config.NormalizeEnabled` and each `entry.NormalizeGainDb` through.

In `src/Soundpad/Api/PlaybackEndpoints.cs`, add the endpoint in `Map` next to `/api/monitor`:

```csharp
g.MapPost("/normalize", (NormalizeBody body, SoundLibrary lib, StateHub hub, HttpContext http) =>
{
    lib.SetNormalizeEnabled(body.Enabled);
    _ = hub.BroadcastAsync("normalizeChanged", new { enabled = body.Enabled }, OriginIdOf(http));
    return Results.NoContent();
});
```

Add the body record alongside the others (e.g. `MonitorBody`):

```csharp
public record NormalizeBody(bool Enabled);
```

(`OriginIdOf(http)` is the existing helper used by `/api/volume` etc.; reuse it.)

- [ ] **Step 4: Run, verify passes**

Run: `dotnet test --filter NormalizeEndpointTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Soundpad/Api/Dto/StateDto.cs src/Soundpad/Api/PlaybackEndpoints.cs tests/Soundpad.Tests/Api/NormalizeEndpointTests.cs
git commit -m "feat(api): /api/normalize toggle + normalize fields in state"
```

---

## Task 6: WPF — global normalize toggle

**Files:**
- Modify: `src/Soundpad/Wpf/MainWindow.cs`

- [ ] **Step 1: Add a "Normalizar volume" checkbox to the bottom bar**

In `MainWindow.cs`, in the bottom-controls bar construction (near the monitor toggle `_monitorCheck`), add a sibling `CheckBox`:

```csharp
var normalizeCheck = new CheckBox
{
    Content = "Normalizar volume",
    Foreground = new SolidColorBrush(MutedTextColor),
    VerticalAlignment = VerticalAlignment.Center,
    Margin = new Thickness(16, 0, 0, 0),
    IsChecked = _library.Config.NormalizeEnabled,
};
normalizeCheck.Checked += (_, _) => _library.SetNormalizeEnabled(true);
normalizeCheck.Unchecked += (_, _) => _library.SetNormalizeEnabled(false);
```

Add `normalizeCheck` to the same container the monitor checkbox lives in.

- [ ] **Step 2: Build + manual check**

Run: `dotnet build src/Soundpad`
Expected: clean. Manual: the WPF bottom bar shows "Normalizar volume" checked by default; toggling it persists (visible in `%LocalAppData%\Reson\config.json` → `"normalizeEnabled": false`).

- [ ] **Step 3: Commit**

```bash
git add src/Soundpad/Wpf/MainWindow.cs
git commit -m "feat(wpf): global normalize-volume toggle"
```

---

## Task 7: Web — global normalize toggle

**Files:**
- Modify: `src/Soundpad/wwwroot/index.html`
- Modify: `src/Soundpad/wwwroot/app.js`

- [ ] **Step 1: Add the toggle to the header and wire it**

In `index.html`, next to the monitor toggle, add:

```html
<label class="norm-toggle"><input type="checkbox" id="normalize" /> Normalizar</label>
```

In `app.js`, in `render()` set its state from `state.normalizeEnabled`:

```js
document.getElementById('normalize').checked = state.normalizeEnabled;
```

And wire the change handler (near the monitor handler):

```js
document.getElementById('normalize').addEventListener('change', e => {
  api('/api/normalize', { method: 'POST', body: JSON.stringify({ enabled: e.target.checked }) });
});
```

Handle the `normalizeChanged` WS event in the `onmessage` switch:

```js
case 'normalizeChanged': state.normalizeEnabled = payload.enabled; render(); break;
```

- [ ] **Step 2: Build + manual check**

Run: `dotnet build src/Soundpad`
Expected: clean (static files only). Manual: phone/browser shows the "Normalizar" checkbox reflecting server state; toggling persists and syncs to the WPF checkbox via WS.

- [ ] **Step 3: Commit**

```bash
git add src/Soundpad/wwwroot/index.html src/Soundpad/wwwroot/app.js
git commit -m "feat(web): global normalize-volume toggle"
```

---

## Task 8: Mobile — global normalize toggle

**Files:**
- Modify: `mobile/reson_app/lib/api/models.dart` (parse `normalizeEnabled`)
- Modify: `mobile/reson_app/lib/api/api_client.dart` (`setNormalize`)
- Modify: `mobile/reson_app/lib/settings/settings_screen.dart` (toggle)

- [ ] **Step 1: Parse the field + add the API method**

In `models.dart`, add `bool normalizeEnabled` to the state model (default `true`) and parse it from `/api/state` JSON (`json['normalizeEnabled'] ?? true`).

In `api_client.dart`, add:

```dart
Future<void> setNormalize(bool enabled) async {
  final r = await _http
      .post(_u('/api/normalize'), headers: _jsonHeaders(), body: jsonEncode({'enabled': enabled}))
      .timeout(_timeout);
  if (r.statusCode != 204 && r.statusCode != 200) throw ApiException(r.statusCode, r.body);
}
```

The body sends exactly `{ "enabled": bool }` — the same contract the WPF and web toggles use, matching the server's `NormalizeBody(bool Enabled)`. (An earlier draft also sent a redundant `value` key "to be safe"; removed — the contract is fixed, so don't send fields the server doesn't read.)

- [ ] **Step 2: Add a Settings toggle**

In `settings_screen.dart`, add a `SwitchListTile` "Normalizar volume" bound to the parsed `normalizeEnabled`, calling `api.setNormalize(v)` on change and refetching state.

- [ ] **Step 3: Build (verify Android compiles)**

Run: `cd mobile/reson_app && flutter analyze lib/api/models.dart lib/api/api_client.dart lib/settings/settings_screen.dart`
Expected: no errors in the changed files. (Full APK build happens at release time via `publish-release.ps1`.)

- [ ] **Step 4: Commit**

```bash
git add mobile/reson_app/lib/
git commit -m "feat(mobile): global normalize-volume toggle"
```

---

## Self-review checklist (run after implementing)

- **Spec coverage:** F1 requires "normalização de volume" with a global toggle + per-sound gain stored on `SoundEntry` (schema v5), applied in the engine. Tasks 1-5 cover backend; 6-8 the tri-platform toggle. ✓
- **Schema:** v4→v5 is additive (nullable gain + bool default true) — no data transform, matches the roadmap's "additive field" migration rule. ✓
- **Type consistency:** `NormalizeGainDb` (double?) and `NormalizeEnabled` (bool) used identically across model, library, engine, DTO, and the three UIs. The WS event is `normalizeChanged { enabled }` everywhere.
- **Limiter interplay (directional):** normalization can boost up to +12 dB, which *increases pressure on the limiter*. The existing hard-clamp prevents overflow, so F1 is safe (no corruption) before T6 — but boosting into a hard clamp *distorts*, which is the exact symptom T6 fixes. So F1 doesn't just coexist with T6, it makes T6 more necessary. **Do T6 before F1 within M1** (see Scope note) so boosts meet a soft knee. If shipped F1-first, expect temporary added distortion on a minority of quiet-but-peaky clips until T6 lands.

## Notes for the implementer

- **Per-sound normalize UI is deferred.** F1 ships only the *global* toggle. A per-sound "renormalize / override" control is a later enhancement (could fold into the F2 editor). The per-sound gain is computed automatically; users don't manage it individually in v1.
- **First-play timing:** the gain is *computed* on the audio thread during the first `HandlePlay` of each sound — a single pass over an in-memory float array (a few ms for a short clip), and the computed value is applied to that play immediately. The *persist* (`SetNormalizeGainDb` → `SaveLocked`, which does disk I/O) is **dispatched off the audio thread via `Task.Run`** (see Task 4 Step 3), so the synchronous file write never blocks playback. This is deliberately *not* the same pattern as the existing `RecordPlay` persist (which still writes on the audio thread — a pre-existing issue out of scope here); F1 does not copy that pattern, it avoids it.
- **Recompute:** if a user replaces a file but keeps the entry, the stored gain is stale. Out of scope for F1; the F2 editor (which creates trim metadata) is the natural place to add a "recompute normalization" action later.
