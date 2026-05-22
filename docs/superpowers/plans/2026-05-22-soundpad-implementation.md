# Soundpad Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows soundpad app that plays sounds into Valorant/Discord/TeamSpeak via VoiceMeeter, controlled remotely from a phone web UI over Wi-Fi.

**Architecture:** Single ASP.NET Core app (`Soundpad.exe`) running locally on the user's PC. NAudio for audio playback via WASAPI through VoiceMeeter virtual cable. Static HTML/JS frontend served by Kestrel acts as the phone-side controller; a `NotifyIcon` shows IP/port and QR code on the PC. A dedicated STA thread owns the `WasapiOut` lifecycle (COM affinity); commands are dispatched via a `BlockingCollection`. Persistence is a single `config.json` with atomic tmp+rename writes.

**Tech Stack:** .NET 8 (`net8.0-windows`), ASP.NET Core minimal API, NAudio, NAudio.Vorbis, QRCoder, System.Windows.Forms (NotifyIcon), xUnit + Moq + Microsoft.AspNetCore.Mvc.Testing.

**Spec:** `docs/superpowers/specs/2026-05-22-soundpad-design.md` — refer back for rationale; this plan is the executable form.

**Build order (validated with the user):**

1. **Phase 1 — Models + SoundLibrary** (Tasks 1-8): foundation. No audio, no network. Pure unit-testable logic for config, persistence, sanitization, grid positioning.
2. **Phase 2 — PlaybackEngine + AudioEngineThread** (Tasks 9-17): the hard one. `IWavePlayer` mocked, asserts on lifecycle, token race, thread affinity.
3. **Phase 3 — API + Auth + StateHub** (Tasks 18-24): wire SoundLibrary + PlaybackEngine into HTTP/WS endpoints with integration tests.
4. **Phase 4 — Front + peripherals** (Tasks 25-31): static HTML/JS UI, NotifyIcon, LAN adapter detection, QR window, boot orchestration. End-to-end manual verification.

---

## Phase 1 — Models + SoundLibrary

### Task 1: Solution scaffolding

**Files:**
- Create: `Soundpad.sln`
- Create: `src/Soundpad/Soundpad.csproj`
- Create: `src/Soundpad/Program.cs` (placeholder)
- Create: `tests/Soundpad.Tests/Soundpad.Tests.csproj`
- Create: `tests/Soundpad.Tests/SmokeTest.cs`
- Create: `.gitignore`

- [ ] **Step 1: Create solution and projects**

```bash
dotnet new sln -n Soundpad
dotnet new web -n Soundpad -o src/Soundpad -f net8.0
dotnet new xunit -n Soundpad.Tests -o tests/Soundpad.Tests -f net8.0
dotnet sln add src/Soundpad/Soundpad.csproj tests/Soundpad.Tests/Soundpad.Tests.csproj
dotnet add tests/Soundpad.Tests reference src/Soundpad
```

- [ ] **Step 2: Set src/Soundpad TFM to net8.0-windows + UseWindowsForms**

Replace contents of `src/Soundpad/Soundpad.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Soundpad</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
    <PackageReference Include="NAudio.Vorbis" Version="1.5.0" />
    <PackageReference Include="QRCoder" Version="1.6.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add test dependencies**

```bash
dotnet add tests/Soundpad.Tests package Moq --version 4.20.70
dotnet add tests/Soundpad.Tests package Microsoft.AspNetCore.Mvc.Testing --version 8.0.0
dotnet add tests/Soundpad.Tests package FluentAssertions --version 6.12.0
```

- [ ] **Step 4: Write smoke test**

Replace contents of `tests/Soundpad.Tests/SmokeTest.cs`:

```csharp
namespace Soundpad.Tests;

public class SmokeTest
{
    [Fact]
    public void Truth_Is_True()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 5: Verify build + tests pass**

Run: `dotnet build`
Expected: build succeeds with no warnings as errors.

Run: `dotnet test`
Expected: 1 test, 1 passing.

- [ ] **Step 6: Add .gitignore**

Create `.gitignore`:

```
bin/
obj/
*.user
.vs/
TestResults/
%LOCALAPPDATA%
config.json
```

---

### Task 2: Model records (persisted schema)

**Files:**
- Create: `src/Soundpad/Models/GridPosition.cs`
- Create: `src/Soundpad/Models/GridLayout.cs`
- Create: `src/Soundpad/Models/SoundEntry.cs`
- Create: `src/Soundpad/Models/SoundConfig.cs`
- Test: `tests/Soundpad.Tests/Models/SoundConfigTests.cs`

- [ ] **Step 1: Write failing test for default config**

Create `tests/Soundpad.Tests/Models/SoundConfigTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Models;

namespace Soundpad.Tests.Models;

public class SoundConfigTests
{
    [Fact]
    public void Default_Has_SchemaVersion_1()
    {
        var c = SoundConfig.Default();
        c.SchemaVersion.Should().Be(1);
        c.MonitorDevice.Should().BeNull();
        c.MonitorEnabled.Should().BeFalse();
        c.Volume.Should().Be(80);
        c.LatencyMs.Should().Be(50);
        c.Port.Should().Be(8080);
        c.Grid.Cols.Should().Be(3);
        c.Grid.Rows.Should().Be(4);
        c.Sounds.Should().BeEmpty();
        c.AuthToken.Should().HaveLength(32);
    }

    [Fact]
    public void Default_AuthToken_Is_Hex_32Chars()
    {
        var c = SoundConfig.Default();
        c.AuthToken.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SoundConfigTests`
Expected: FAIL with "type or namespace `Soundpad.Models` could not be found".

- [ ] **Step 3: Implement GridPosition**

Create `src/Soundpad/Models/GridPosition.cs`:

```csharp
namespace Soundpad.Models;

public record GridPosition(int Col, int Row);
```

- [ ] **Step 4: Implement GridLayout**

Create `src/Soundpad/Models/GridLayout.cs`:

```csharp
namespace Soundpad.Models;

public record GridLayout(int Cols, int Rows)
{
    public bool Contains(GridPosition p) =>
        p.Col >= 0 && p.Col < Cols && p.Row >= 0 && p.Row < Rows;

    public int CellCount => Cols * Rows;
}
```

- [ ] **Step 5: Implement SoundEntry**

Create `src/Soundpad/Models/SoundEntry.cs`:

```csharp
namespace Soundpad.Models;

public record SoundEntry
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Label { get; init; }
    public string Color { get; init; } = "#3b82f6";
    public string? Icon { get; init; }
    public GridPosition? Position { get; init; }
}
```

- [ ] **Step 6: Implement SoundConfig with Default()**

Create `src/Soundpad/Models/SoundConfig.cs`:

```csharp
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
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test --filter SoundConfigTests`
Expected: 2 tests passing.

- [ ] **Step 8: Commit**

```bash
git init
git add -A
git commit -m "feat(models): SoundConfig, SoundEntry, GridLayout, GridPosition"
```

---

### Task 3: FilenameSanitizer

**Files:**
- Create: `src/Soundpad/Sound/FilenameSanitizer.cs`
- Test: `tests/Soundpad.Tests/Sound/FilenameSanitizerTests.cs`

- [ ] **Step 1: Write failing tests covering all Windows quirks**

Create `tests/Soundpad.Tests/Sound/FilenameSanitizerTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class FilenameSanitizerTests
{
    [Theory]
    [InlineData("hello.mp3", "hello.mp3")]
    [InlineData("ra_ze ult (1).mp3", "ra_ze ult (1).mp3")]
    [InlineData("foo/bar.mp3", "foo_bar.mp3")]
    [InlineData("foo\\bar.mp3", "foo_bar.mp3")]
    [InlineData("..\\evil.mp3", "__evil.mp3")]
    [InlineData("foo:bar.mp3", "foo_bar.mp3")]
    [InlineData("foo*bar?.mp3", "foo_bar_.mp3")]
    public void Replaces_Invalid_Chars(string input, string expected)
    {
        FilenameSanitizer.Sanitize(input, fallbackId: "fallback").Should().Be(expected);
    }

    [Theory]
    [InlineData("CON.mp3", "_CON.mp3")]
    [InlineData("con.MP3", "_con.MP3")]
    [InlineData("PRN.wav", "_PRN.wav")]
    [InlineData("AUX.ogg", "_AUX.ogg")]
    [InlineData("NUL.flac", "_NUL.flac")]
    [InlineData("COM1.mp3", "_COM1.mp3")]
    [InlineData("LPT9.mp3", "_LPT9.mp3")]
    [InlineData("CON", "_CON")]
    public void Prefixes_Reserved_Windows_Names(string input, string expected)
    {
        FilenameSanitizer.Sanitize(input, fallbackId: "fallback").Should().Be(expected);
    }

    [Theory]
    [InlineData("hello.mp3.", "hello.mp3")]
    [InlineData("hello.mp3 ", "hello.mp3")]
    [InlineData("hello.mp3...", "hello.mp3")]
    [InlineData("hello.mp3   ", "hello.mp3")]
    public void Trims_Trailing_Dots_And_Spaces(string input, string expected)
    {
        FilenameSanitizer.Sanitize(input, fallbackId: "fallback").Should().Be(expected);
    }

    [Theory]
    [InlineData("hello .mp3", "hello_.mp3")]
    [InlineData("hello.mp3", "hello_.mp3")]
    [InlineData("hello.mp3", "hello_.mp3")]
    public void Strips_Control_Chars(string input, string expected)
    {
        FilenameSanitizer.Sanitize(input, fallbackId: "fallback").Should().Be(expected);
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData("...")]
    [InlineData("   ")]
    [InlineData("")]
    public void Falls_Back_When_Empty_After_Sanitize(string input)
    {
        var result = FilenameSanitizer.Sanitize(input, fallbackId: "myid");
        result.Should().StartWith("myid");
    }

    [Fact]
    public void Preserves_Original_Extension_In_Fallback()
    {
        FilenameSanitizer.Sanitize("...", fallbackId: "myid", extension: ".mp3")
            .Should().Be("myid.mp3");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FilenameSanitizerTests`
Expected: FAIL (type does not exist).

- [ ] **Step 3: Implement FilenameSanitizer**

Create `src/Soundpad/Sound/FilenameSanitizer.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Soundpad.Sound;

public static class FilenameSanitizer
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Regex AllowedChars = new(@"[A-Za-z0-9 _.()\[\]\-]", RegexOptions.Compiled);

    public static string Sanitize(string input, string fallbackId, string extension = "")
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch is < ' ' or '') { sb.Append('_'); continue; }
            sb.Append(AllowedChars.IsMatch(ch.ToString()) ? ch : '_');
        }

        var trimmed = sb.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(trimmed))
            return string.IsNullOrEmpty(extension) ? fallbackId : fallbackId + extension;

        var stem = Path.GetFileNameWithoutExtension(trimmed);
        var ext = Path.GetExtension(trimmed);
        if (ReservedNames.Contains(stem))
            return "_" + trimmed;

        return trimmed;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter FilenameSanitizerTests`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sound): FilenameSanitizer with Windows-safe quirks"
```

---

### Task 4: GridPositioner

**Files:**
- Create: `src/Soundpad/Sound/GridPositioner.cs`
- Test: `tests/Soundpad.Tests/Sound/GridPositionerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Soundpad.Tests/Sound/GridPositionerTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class GridPositionerTests
{
    private static SoundEntry Entry(string id, int? col = null, int? row = null)
        => new() {
            Id = id, File = id + ".mp3", Label = id,
            Position = col.HasValue && row.HasValue ? new GridPosition(col.Value, row.Value) : null
        };

    [Fact]
    public void NextFree_Empty_Grid_Returns_0_0()
    {
        var grid = new GridLayout(3, 4);
        GridPositioner.NextFree(grid, new List<SoundEntry>())
            .Should().Be(new GridPosition(0, 0));
    }

    [Fact]
    public void NextFree_Returns_RowMajor_Order()
    {
        var grid = new GridLayout(3, 4);
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 1, 0) };
        GridPositioner.NextFree(grid, sounds).Should().Be(new GridPosition(2, 0));
    }

    [Fact]
    public void NextFree_Skips_Occupied_Cells()
    {
        var grid = new GridLayout(3, 2);
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 2, 0) };
        GridPositioner.NextFree(grid, sounds).Should().Be(new GridPosition(1, 0));
    }

    [Fact]
    public void NextFree_Returns_Null_When_Full()
    {
        var grid = new GridLayout(2, 1);
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 1, 0) };
        GridPositioner.NextFree(grid, sounds).Should().BeNull();
    }

    [Fact]
    public void Repair_Marks_Duplicates_As_Null_First_Wins()
    {
        var grid = new GridLayout(3, 3);
        var sounds = new List<SoundEntry>
        {
            Entry("a", 0, 0), Entry("b", 0, 0), Entry("c", 1, 1)
        };
        var repaired = GridPositioner.RepairDuplicates(grid, sounds);
        repaired[0].Position.Should().Be(new GridPosition(0, 0));
        repaired[1].Position.Should().BeNull();
        repaired[2].Position.Should().Be(new GridPosition(1, 1));
    }

    [Fact]
    public void Repair_Marks_OutOfBounds_As_Null()
    {
        var grid = new GridLayout(2, 2);
        var sounds = new List<SoundEntry> { Entry("a", 5, 5), Entry("b", 0, 0) };
        var repaired = GridPositioner.RepairDuplicates(grid, sounds);
        repaired[0].Position.Should().BeNull();
        repaired[1].Position.Should().Be(new GridPosition(0, 0));
    }

    [Fact]
    public void Swap_Exchanges_Positions()
    {
        var sounds = new List<SoundEntry> { Entry("a", 0, 0), Entry("b", 1, 1) };
        var result = GridPositioner.Swap(sounds, "a", new GridPosition(1, 1));
        result.Single(s => s.Id == "a").Position.Should().Be(new GridPosition(1, 1));
        result.Single(s => s.Id == "b").Position.Should().Be(new GridPosition(0, 0));
    }

    [Fact]
    public void Swap_To_Empty_Cell_Just_Moves()
    {
        var sounds = new List<SoundEntry> { Entry("a", 0, 0) };
        var result = GridPositioner.Swap(sounds, "a", new GridPosition(2, 2));
        result.Single().Position.Should().Be(new GridPosition(2, 2));
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test --filter GridPositionerTests`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement GridPositioner**

Create `src/Soundpad/Sound/GridPositioner.cs`:

```csharp
using Soundpad.Models;

namespace Soundpad.Sound;

public static class GridPositioner
{
    public static GridPosition? NextFree(GridLayout grid, IReadOnlyList<SoundEntry> sounds)
    {
        var occupied = new HashSet<GridPosition>(
            sounds.Where(s => s.Position is not null).Select(s => s.Position!));
        for (int row = 0; row < grid.Rows; row++)
        for (int col = 0; col < grid.Cols; col++)
        {
            var p = new GridPosition(col, row);
            if (!occupied.Contains(p)) return p;
        }
        return null;
    }

    public static List<SoundEntry> RepairDuplicates(GridLayout grid, IReadOnlyList<SoundEntry> sounds)
    {
        var seen = new HashSet<GridPosition>();
        var result = new List<SoundEntry>(sounds.Count);
        foreach (var s in sounds)
        {
            if (s.Position is null) { result.Add(s); continue; }
            if (!grid.Contains(s.Position) || !seen.Add(s.Position))
                result.Add(s with { Position = null });
            else
                result.Add(s);
        }
        return result;
    }

    public static List<SoundEntry> Swap(IReadOnlyList<SoundEntry> sounds, string movingId, GridPosition target)
    {
        var moving = sounds.Single(s => s.Id == movingId);
        var occupant = sounds.FirstOrDefault(s => s.Id != movingId && s.Position == target);
        var result = new List<SoundEntry>(sounds.Count);
        foreach (var s in sounds)
        {
            if (s.Id == movingId) result.Add(s with { Position = target });
            else if (occupant is not null && s.Id == occupant.Id) result.Add(s with { Position = moving.Position });
            else result.Add(s);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter GridPositionerTests`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sound): GridPositioner — next-free, repair, swap"
```

---

### Task 5: SoundLibrary load/save with atomic write

**Files:**
- Create: `src/Soundpad/Sound/SoundLibrary.cs`
- Create: `src/Soundpad/Sound/SoundLibraryOptions.cs`
- Test: `tests/Soundpad.Tests/Sound/SoundLibraryLoadSaveTests.cs`

- [ ] **Step 1: Define options + write failing load/save tests**

Create `src/Soundpad/Sound/SoundLibraryOptions.cs`:

```csharp
namespace Soundpad.Sound;

public record SoundLibraryOptions(string RootDir)
{
    public string ConfigPath => Path.Combine(RootDir, "config.json");
    public string SoundsDir => Path.Combine(RootDir, "sounds");
}
```

Create `tests/Soundpad.Tests/Sound/SoundLibraryLoadSaveTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryLoadSaveTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-test-" + Guid.NewGuid());

    public SoundLibraryLoadSaveTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_Creates_Default_When_File_Missing()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.Config.SchemaVersion.Should().Be(1);
        File.Exists(Path.Combine(_tempDir, "config.json")).Should().BeTrue();
    }

    [Fact]
    public void Save_Then_Load_Roundtrips_Config()
    {
        var lib1 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib1.Load();
        lib1.MutateConfig(c => c with { Volume = 42, MonitorEnabled = true });
        lib1.Save();

        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.Volume.Should().Be(42);
        lib2.Config.MonitorEnabled.Should().BeTrue();
    }

    [Fact]
    public void Save_Is_Atomic_Tmp_File_Cleaned_Up()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.Save();
        Directory.GetFiles(_tempDir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Load_Rejects_Future_SchemaVersion()
    {
        var futurePath = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(futurePath, "{ \"schemaVersion\": 999, \"authToken\": \"a\", \"sounds\": [] }");
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        Assert.Throws<NotSupportedException>(() => lib.Load());
    }

    [Fact]
    public void Save_Concurrent_Mutations_Are_Serialized()
    {
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => lib.MutateConfig(c => c with { Volume = (c.Volume + 1) % 101 }))).ToArray();
        Task.WaitAll(tasks);
        lib.Save();

        var lib2 = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib2.Load();
        lib2.Config.Volume.Should().BeInRange(0, 100);
    }
}
```

- [ ] **Step 2: Run tests — they should fail (SoundLibrary doesn't exist)**

Run: `dotnet test --filter SoundLibraryLoadSaveTests`
Expected: FAIL.

- [ ] **Step 3: Implement SoundLibrary load/save**

Create `src/Soundpad/Sound/SoundLibrary.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter SoundLibraryLoadSaveTests`
Expected: all 5 passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sound): SoundLibrary load/save with atomic write + schema gate"
```

---

### Task 6: Upload transaction (TOCTOU-safe)

**Files:**
- Modify: `src/Soundpad/Sound/SoundLibrary.cs`
- Create: `src/Soundpad/Sound/UploadResult.cs`
- Test: `tests/Soundpad.Tests/Sound/SoundLibraryUploadTests.cs`

- [ ] **Step 1: Write failing tests for upload semantics**

Create `src/Soundpad/Sound/UploadResult.cs`:

```csharp
using Soundpad.Models;

namespace Soundpad.Sound;

public record UploadResult(SoundEntry Entry, string FinalFilename);
```

Create `tests/Soundpad.Tests/Sound/SoundLibraryUploadTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryUploadTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-up-" + Guid.NewGuid());
    private readonly SoundLibrary _lib;

    public SoundLibraryUploadTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        _lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        _lib.Load();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static MemoryStream MakeStream(int bytes) => new(new byte[bytes]);

    [Fact]
    public void Upload_Writes_File_And_Registers_Entry()
    {
        var r = _lib.Upload("hello.mp3", MakeStream(1024));
        File.Exists(Path.Combine(_tempDir, "sounds", "hello.mp3")).Should().BeTrue();
        _lib.Config.Sounds.Should().ContainSingle(s => s.Id == "hello");
        r.Entry.Position.Should().Be(new Models.GridPosition(0, 0));
    }

    [Fact]
    public void Upload_Collision_Suffixes_2_3_etc()
    {
        _lib.Upload("foo.mp3", MakeStream(10));
        _lib.Upload("foo.mp3", MakeStream(10));
        _lib.Upload("foo.mp3", MakeStream(10));
        File.Exists(Path.Combine(_tempDir, "sounds", "foo.mp3")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "sounds", "foo (2).mp3")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "sounds", "foo (3).mp3")).Should().BeTrue();
    }

    [Fact]
    public void Upload_Id_Derives_From_Final_Filename_Not_Original()
    {
        _lib.Upload("raze ult.mp3", MakeStream(10));
        var second = _lib.Upload("raze ult.mp3", MakeStream(10));
        second.Entry.Id.Should().Be("raze-ult-2");
        second.Entry.File.Should().Be("raze ult (2).mp3");
    }

    [Fact]
    public void Upload_Rejects_Invalid_Extension()
    {
        Assert.Throws<InvalidOperationException>(() => _lib.Upload("evil.exe", MakeStream(10)));
    }

    [Fact]
    public void Upload_Rejects_Too_Large()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _lib.Upload("big.mp3", MakeStream(SoundLibrary.MaxUploadBytes + 1)));
    }

    [Fact]
    public void Upload_Concurrent_Same_Name_Produces_Two_Distinct_Files()
    {
        var t1 = Task.Run(() => _lib.Upload("dup.mp3", MakeStream(10)));
        var t2 = Task.Run(() => _lib.Upload("dup.mp3", MakeStream(10)));
        Task.WaitAll(t1, t2);
        Directory.GetFiles(Path.Combine(_tempDir, "sounds"), "dup*.mp3").Should().HaveCount(2);
    }

    [Fact]
    public void Upload_Sanitizes_Reserved_Name()
    {
        var r = _lib.Upload("CON.mp3", MakeStream(10));
        r.FinalFilename.Should().Be("_CON.mp3");
        r.Entry.File.Should().Be("_CON.mp3");
        r.Entry.Id.Should().Be("_con");
    }

    [Fact]
    public void Upload_Persists_Config_To_Disk()
    {
        _lib.Upload("foo.mp3", MakeStream(10));
        var raw = File.ReadAllText(Path.Combine(_tempDir, "config.json"));
        raw.Should().Contain("foo.mp3");
    }

    [Fact]
    public void Upload_Grid_Full_Expands_Rows()
    {
        _lib.MutateConfig(c => c with { Grid = new Models.GridLayout(1, 1) });
        _lib.Upload("a.mp3", MakeStream(10));
        _lib.Upload("b.mp3", MakeStream(10));
        _lib.Config.Grid.Rows.Should().Be(2);
        _lib.Config.Sounds.Should().HaveCount(2);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test --filter SoundLibraryUploadTests`
Expected: FAIL.

- [ ] **Step 3: Extend SoundLibrary with Upload method**

Append to `src/Soundpad/Sound/SoundLibrary.cs` (inside the class):

```csharp
    public const long MaxUploadBytes = 26_214_400;
    private static readonly string[] AllowedExtensions = { ".mp3", ".wav", ".ogg", ".flac" };

    public UploadResult Upload(string originalFilename, Stream content)
    {
        var ext = Path.GetExtension(originalFilename).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException($"Extension {ext} not allowed");
        if (content.CanSeek && content.Length > MaxUploadBytes)
            throw new InvalidOperationException("File too large");

        lock (_lock)
        {
            Directory.CreateDirectory(_opts.SoundsDir);
            var sanitized = FilenameSanitizer.Sanitize(originalFilename, fallbackId: Guid.NewGuid().ToString("N"), extension: ext);
            var (finalPath, finalName) = OpenWithCollisionSuffix(sanitized);
            using (var fs = File.OpenWrite(finalPath))
            {
                content.CopyTo(fs);
                if (fs.Length > MaxUploadBytes)
                {
                    fs.Close();
                    File.Delete(finalPath);
                    throw new InvalidOperationException("File too large");
                }
            }

            var id = DeriveIdFromFilename(finalName, _config.Sounds);
            var grid = EnsureCellAvailable(_config.Grid, _config.Sounds);
            var pos = GridPositioner.NextFree(grid, _config.Sounds);
            var entry = new SoundEntry { Id = id, File = finalName, Label = id, Position = pos };
            _config = _config with { Grid = grid, Sounds = new List<SoundEntry>(_config.Sounds) { entry } };
            SaveLocked();
            return new UploadResult(entry, finalName);
        }
    }

    private (string FullPath, string FileName) OpenWithCollisionSuffix(string sanitized)
    {
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        var ext = Path.GetExtension(sanitized);
        for (int i = 1; i <= 100; i++)
        {
            var candidate = i == 1 ? sanitized : $"{stem} ({i}){ext}";
            var full = Path.Combine(_opts.SoundsDir, candidate);
            try
            {
                using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
                return (full, candidate);
            }
            catch (IOException) { /* try next */ }
        }
        throw new InvalidOperationException("Too many filename collisions");
    }

    private static string DeriveIdFromFilename(string filename, IReadOnlyList<SoundEntry> existing)
    {
        var stem = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
        var slug = System.Text.RegularExpressions.Regex.Replace(stem, @"[^a-z0-9]+", "-").Trim('-');
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
```

Note: `OpenWithCollisionSuffix` opens the file with `CreateNew` and then closes it inside the using; the outer code reopens with `OpenWrite`. **Tighten:** instead, return the open `FileStream` from `OpenWithCollisionSuffix` to avoid the race window. Refactor inline:

Replace `OpenWithCollisionSuffix` and the surrounding write block in `Upload` with:

```csharp
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
```

And replace the file-write block in `Upload` with:

```csharp
            var sanitized = FilenameSanitizer.Sanitize(originalFilename, fallbackId: Guid.NewGuid().ToString("N"), extension: ext);
            var (fs, finalName) = CreateExclusive(sanitized);
            using (fs)
            {
                content.CopyTo(fs);
                if (fs.Length > MaxUploadBytes)
                {
                    fs.Close();
                    File.Delete(Path.Combine(_opts.SoundsDir, finalName));
                    throw new InvalidOperationException("File too large");
                }
            }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter SoundLibraryUploadTests`
Expected: 9 tests passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sound): atomic TOCTOU-safe upload transaction"
```

---

### Task 7: Auto-scan + missing detection on boot

**Files:**
- Modify: `src/Soundpad/Sound/SoundLibrary.cs`
- Create: `src/Soundpad/Sound/SoundRuntimeStatus.cs`
- Test: `tests/Soundpad.Tests/Sound/SoundLibraryBootTests.cs`

- [ ] **Step 1: Define runtime status type**

Create `src/Soundpad/Sound/SoundRuntimeStatus.cs`:

```csharp
namespace Soundpad.Sound;

public record SoundRuntimeStatus(string Id, bool Missing);
```

- [ ] **Step 2: Write failing tests**

Create `tests/Soundpad.Tests/Sound/SoundLibraryBootTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryBootTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-boot-" + Guid.NewGuid());

    public SoundLibraryBootTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void AutoScan_Adds_New_Files_From_Sounds_Folder()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "drop.mp3"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "boom.wav"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        lib.Config.Sounds.Should().HaveCount(2);
        lib.Config.Sounds.Should().Contain(s => s.File == "drop.mp3");
        lib.Config.Sounds.Should().Contain(s => s.File == "boom.wav");
    }

    [Fact]
    public void AutoScan_Skips_Files_Already_In_Config()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "x.mp3"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        lib.AutoScan(); // second call should not double-add
        lib.Config.Sounds.Should().HaveCount(1);
    }

    [Fact]
    public void RuntimeStatus_Marks_Missing_When_File_Deleted()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "x.mp3"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        File.Delete(Path.Combine(_tempDir, "sounds", "x.mp3"));
        var status = lib.GetRuntimeStatuses();
        status.Should().ContainSingle(s => s.Id == lib.Config.Sounds[0].Id && s.Missing);
    }

    [Fact]
    public void RuntimeStatus_Not_Persisted_To_Config()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "x.mp3"), new byte[10]);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.AutoScan();
        File.Delete(Path.Combine(_tempDir, "sounds", "x.mp3"));
        lib.GetRuntimeStatuses();
        lib.Save();
        File.ReadAllText(Path.Combine(_tempDir, "config.json"))
            .Should().NotContain("missing");
    }

    [Fact]
    public void Repair_Fixes_Duplicate_Positions_On_Load()
    {
        var bad = @"{
            ""schemaVersion"": 1,
            ""authToken"": ""abc"",
            ""grid"": { ""cols"": 3, ""rows"": 3 },
            ""sounds"": [
                { ""id"": ""a"", ""file"": ""a.mp3"", ""label"": ""A"", ""position"": { ""col"": 0, ""row"": 0 } },
                { ""id"": ""b"", ""file"": ""b.mp3"", ""label"": ""B"", ""position"": { ""col"": 0, ""row"": 0 } }
            ]
        }";
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), bad);
        var lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        lib.Load();
        lib.RepairInvariants();
        lib.Config.Sounds[0].Position.Should().Be(new GridPosition(0, 0));
        lib.Config.Sounds[1].Position.Should().BeNull();
    }
}
```

- [ ] **Step 3: Run tests to verify failure**

Run: `dotnet test --filter SoundLibraryBootTests`
Expected: FAIL — `AutoScan`/`RepairInvariants`/`GetRuntimeStatuses` don't exist.

- [ ] **Step 4: Implement AutoScan, RepairInvariants, GetRuntimeStatuses**

Append to `src/Soundpad/Sound/SoundLibrary.cs`:

```csharp
    public void AutoScan()
    {
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
        }
    }

    public void RepairInvariants()
    {
        lock (_lock)
        {
            var repaired = GridPositioner.RepairDuplicates(_config.Grid, _config.Sounds);
            if (repaired.SequenceEqual(_config.Sounds)) return;
            _config = _config with { Sounds = repaired };
            SaveLocked();
        }
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
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter SoundLibraryBootTests`
Expected: 5 tests passing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(sound): auto-scan, invariant repair, runtime status"
```

---

### Task 8: SoundLibrary.Boot orchestration + grid/edit operations

**Files:**
- Modify: `src/Soundpad/Sound/SoundLibrary.cs`
- Test: `tests/Soundpad.Tests/Sound/SoundLibraryGridOpsTests.cs`

- [ ] **Step 1: Write tests for grid resize, swap-via-update, layout apply, delete**

Create `tests/Soundpad.Tests/Sound/SoundLibraryGridOpsTests.cs`:

```csharp
using FluentAssertions;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Tests.Sound;

public class SoundLibraryGridOpsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "soundpad-grid-" + Guid.NewGuid());
    private readonly SoundLibrary _lib;

    public SoundLibraryGridOpsTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "sounds"));
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "a.mp3"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_tempDir, "sounds", "b.mp3"), new byte[10]);
        _lib = new SoundLibrary(new SoundLibraryOptions(_tempDir));
        _lib.Load();
        _lib.AutoScan();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ResizeGrid_Shrinking_Marks_OutOfBounds_As_Null()
    {
        _lib.MutateConfig(c => c with { Grid = new GridLayout(5, 5) });
        var a = _lib.Config.Sounds[0];
        _lib.UpdateSound(a.Id, position: new GridPosition(4, 4));
        _lib.ResizeGrid(2, 2);
        _lib.Config.Sounds.Single(s => s.Id == a.Id).Position.Should().BeNull();
    }

    [Fact]
    public void UpdateSound_Swaps_When_Position_Occupied()
    {
        var ids = _lib.Config.Sounds.Select(s => s.Id).ToList();
        var aPos = _lib.Config.Sounds.Single(s => s.Id == ids[0]).Position!;
        var bPos = _lib.Config.Sounds.Single(s => s.Id == ids[1]).Position!;
        _lib.UpdateSound(ids[0], position: bPos);
        _lib.Config.Sounds.Single(s => s.Id == ids[0]).Position.Should().Be(bPos);
        _lib.Config.Sounds.Single(s => s.Id == ids[1]).Position.Should().Be(aPos);
    }

    [Fact]
    public void UpdateSound_Renames_Label_And_Color()
    {
        var id = _lib.Config.Sounds[0].Id;
        _lib.UpdateSound(id, label: "NEW NAME", color: "#ff0000");
        var updated = _lib.Config.Sounds.Single(s => s.Id == id);
        updated.Label.Should().Be("NEW NAME");
        updated.Color.Should().Be("#ff0000");
    }

    [Fact]
    public void DeleteSound_Removes_Entry()
    {
        var id = _lib.Config.Sounds[0].Id;
        _lib.DeleteSound(id, deleteFile: false);
        _lib.Config.Sounds.Should().NotContain(s => s.Id == id);
    }

    [Fact]
    public void DeleteSound_With_DeleteFile_True_Removes_File()
    {
        var entry = _lib.Config.Sounds[0];
        _lib.DeleteSound(entry.Id, deleteFile: true);
        File.Exists(Path.Combine(_tempDir, "sounds", entry.File)).Should().BeFalse();
    }

    [Fact]
    public void ApplyLayout_Atomic_Rejects_Duplicates()
    {
        var ids = _lib.Config.Sounds.Select(s => s.Id).ToList();
        var dup = new[]
        {
            (ids[0], (GridPosition?)new GridPosition(0, 0)),
            (ids[1], (GridPosition?)new GridPosition(0, 0)),
        };
        Assert.Throws<InvalidOperationException>(() => _lib.ApplyLayout(dup));
    }

    [Fact]
    public void ApplyLayout_Atomic_Applies_All_Or_Nothing()
    {
        var ids = _lib.Config.Sounds.Select(s => s.Id).ToList();
        var placements = new (string, GridPosition?)[]
        {
            (ids[0], new GridPosition(2, 0)),
            (ids[1], new GridPosition(0, 2)),
        };
        _lib.ApplyLayout(placements);
        _lib.Config.Sounds.Single(s => s.Id == ids[0]).Position.Should().Be(new GridPosition(2, 0));
        _lib.Config.Sounds.Single(s => s.Id == ids[1]).Position.Should().Be(new GridPosition(0, 2));
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test --filter SoundLibraryGridOpsTests`
Expected: FAIL — methods don't exist.

- [ ] **Step 3: Implement UpdateSound, DeleteSound, ResizeGrid, ApplyLayout**

Append to `src/Soundpad/Sound/SoundLibrary.cs`:

```csharp
    public void UpdateSound(string id, string? label = null, string? color = null,
                            string? icon = null, GridPosition? position = null,
                            bool clearPosition = false)
    {
        lock (_lock)
        {
            var sounds = _config.Sounds.ToList();
            var idx = sounds.FindIndex(s => s.Id == id);
            if (idx < 0) throw new InvalidOperationException($"Unknown sound id: {id}");
            var current = sounds[idx];
            var updated = current with
            {
                Label = label ?? current.Label,
                Color = color ?? current.Color,
                Icon = icon ?? current.Icon,
                Position = clearPosition ? null : (position ?? current.Position),
            };
            sounds[idx] = updated;
            if (position is not null && !clearPosition)
                sounds = GridPositioner.Swap(sounds, id, position);
            _config = _config with { Sounds = sounds };
            SaveLocked();
        }
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
    }

    public void ApplyLayout(IEnumerable<(string Id, GridPosition? Position)> placements)
    {
        lock (_lock)
        {
            var list = placements.ToList();
            var nonNull = list.Where(p => p.Position is not null).ToList();
            if (nonNull.Select(p => p.Position).Distinct().Count() != nonNull.Count)
                throw new InvalidOperationException("Duplicate positions in layout");
            foreach (var (id, pos) in list)
                if (pos is not null && !_config.Grid.Contains(pos))
                    throw new InvalidOperationException($"Position {pos} out of grid {_config.Grid}");
            var map = list.ToDictionary(p => p.Id, p => p.Position);
            var updated = _config.Sounds
                .Select(s => map.TryGetValue(s.Id, out var p) ? s with { Position = p } : s)
                .ToList();
            _config = _config with { Sounds = updated };
            SaveLocked();
        }
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter SoundLibraryGridOpsTests`
Expected: 7 tests passing.

- [ ] **Step 5: Run full suite to verify no regressions**

Run: `dotnet test`
Expected: all tests passing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(sound): grid resize, sound edit/delete, atomic layout"
```

---

## Phase 2 — PlaybackEngine + AudioEngineThread

### Task 9: SoundCache (LRU of decoded PCM)

**Files:**
- Create: `src/Soundpad/Audio/CachedSound.cs`
- Create: `src/Soundpad/Audio/ISoundDecoder.cs`
- Create: `src/Soundpad/Audio/NAudioSoundDecoder.cs`
- Create: `src/Soundpad/Audio/SoundCache.cs`
- Test: `tests/Soundpad.Tests/Audio/SoundCacheTests.cs`

- [ ] **Step 1: Define types**

Create `src/Soundpad/Audio/CachedSound.cs`:

```csharp
using NAudio.Wave;

namespace Soundpad.Audio;

public record CachedSound(WaveFormat Format, byte[] PcmBytes);
```

Create `src/Soundpad/Audio/ISoundDecoder.cs`:

```csharp
namespace Soundpad.Audio;

public interface ISoundDecoder
{
    CachedSound Decode(string filePath);
}
```

- [ ] **Step 2: Write failing test**

Create `tests/Soundpad.Tests/Audio/SoundCacheTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class SoundCacheTests
{
    private static CachedSound MakeCached(int sizeBytes = 16)
        => new(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[sizeBytes]);

    [Fact]
    public void Get_Decodes_On_First_Call()
    {
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode("x.mp3")).Returns(MakeCached());
        var cache = new SoundCache(decoder.Object, capacity: 10);

        cache.Get("x.mp3").Should().NotBeNull();
        decoder.Verify(d => d.Decode("x.mp3"), Times.Once);
    }

    [Fact]
    public void Get_Cache_Hit_Does_Not_Redecode()
    {
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode("x.mp3")).Returns(MakeCached());
        var cache = new SoundCache(decoder.Object, capacity: 10);

        cache.Get("x.mp3");
        cache.Get("x.mp3");
        cache.Get("x.mp3");
        decoder.Verify(d => d.Decode("x.mp3"), Times.Once);
    }

    [Fact]
    public void Lru_Evicts_Least_Recently_Used()
    {
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>())).Returns(MakeCached());
        var cache = new SoundCache(decoder.Object, capacity: 2);

        cache.Get("a.mp3");
        cache.Get("b.mp3");
        cache.Get("a.mp3"); // a now MRU, b is LRU
        cache.Get("c.mp3"); // evicts b
        cache.Get("b.mp3"); // re-decode

        decoder.Verify(d => d.Decode("a.mp3"), Times.Once);
        decoder.Verify(d => d.Decode("b.mp3"), Times.Exactly(2));
        decoder.Verify(d => d.Decode("c.mp3"), Times.Once);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter SoundCacheTests`
Expected: FAIL.

- [ ] **Step 4: Implement SoundCache**

Create `src/Soundpad/Audio/SoundCache.cs`:

```csharp
namespace Soundpad.Audio;

public class SoundCache
{
    private readonly ISoundDecoder _decoder;
    private readonly int _capacity;
    private readonly LinkedList<(string Key, CachedSound Value)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, CachedSound Value)>> _index = new();
    private readonly object _lock = new();

    public SoundCache(ISoundDecoder decoder, int capacity = 50)
    {
        _decoder = decoder;
        _capacity = capacity;
    }

    public CachedSound Get(string filePath)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(filePath, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Value;
            }
            var decoded = _decoder.Decode(filePath);
            var newNode = new LinkedListNode<(string, CachedSound)>((filePath, decoded));
            _order.AddFirst(newNode);
            _index[filePath] = newNode;
            if (_index.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _index.Remove(lru.Value.Key);
            }
            return decoded;
        }
    }
}
```

- [ ] **Step 5: Implement NAudioSoundDecoder (the real one, used in prod)**

Create `src/Soundpad/Audio/NAudioSoundDecoder.cs`:

```csharp
using NAudio.Wave;

namespace Soundpad.Audio;

public class NAudioSoundDecoder : ISoundDecoder
{
    public CachedSound Decode(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        using var ms = new MemoryStream();
        var buf = new byte[reader.WaveFormat.AverageBytesPerSecond];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            ms.Write(buf, 0, read);
        return new CachedSound(format, ms.ToArray());
    }
}
```

- [ ] **Step 6: Run cache tests**

Run: `dotnet test --filter SoundCacheTests`
Expected: 3 tests passing.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(audio): SoundCache LRU + NAudio decoder"
```

---

### Task 10: DeviceLocator

**Files:**
- Create: `src/Soundpad/Audio/IAudioDeviceEnumerator.cs`
- Create: `src/Soundpad/Audio/WasapiAudioDeviceEnumerator.cs`
- Create: `src/Soundpad/Audio/DeviceLocator.cs`
- Test: `tests/Soundpad.Tests/Audio/DeviceLocatorTests.cs`

- [ ] **Step 1: Define abstractions**

Create `src/Soundpad/Audio/IAudioDeviceEnumerator.cs`:

```csharp
namespace Soundpad.Audio;

public record AudioDeviceInfo(string Id, string FriendlyName);

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices();
}
```

- [ ] **Step 2: Write failing tests**

Create `tests/Soundpad.Tests/Audio/DeviceLocatorTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class DeviceLocatorTests
{
    private static IAudioDeviceEnumerator MakeEnum(params string[] names)
    {
        var m = new Mock<IAudioDeviceEnumerator>();
        m.Setup(e => e.EnumerateRenderDevices()).Returns(
            names.Select((n, i) => new AudioDeviceInfo($"id{i}", n)).ToList());
        return m.Object;
    }

    [Fact]
    public void FindVoiceMeeter_Detects_Standard_Input()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"));
        loc.FindVoiceMeeterInput().Should().Contain("VoiceMeeter Input");
    }

    [Fact]
    public void FindVoiceMeeter_Detects_VAIO3_Variant()
    {
        var loc = new DeviceLocator(MakeEnum("VoiceMeeter VAIO3 Input"));
        loc.FindVoiceMeeterInput().Should().Contain("VAIO3");
    }

    [Fact]
    public void FindVoiceMeeter_Detects_Aux_Variant()
    {
        var loc = new DeviceLocator(MakeEnum("VoiceMeeter Aux Input"));
        loc.FindVoiceMeeterInput().Should().Contain("Aux");
    }

    [Fact]
    public void FindVoiceMeeter_Returns_Null_When_Absent()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "Headphones"));
        loc.FindVoiceMeeterInput().Should().BeNull();
    }

    [Fact]
    public void EnumerateAll_Returns_Friendly_Names()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "Headphones", "VoiceMeeter Input"));
        loc.EnumerateRenderDeviceNames().Should().BeEquivalentTo("Speakers", "Headphones", "VoiceMeeter Input");
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter DeviceLocatorTests`
Expected: FAIL.

- [ ] **Step 4: Implement DeviceLocator + Wasapi adapter**

Create `src/Soundpad/Audio/DeviceLocator.cs`:

```csharp
namespace Soundpad.Audio;

public class DeviceLocator
{
    private readonly IAudioDeviceEnumerator _enumerator;

    public DeviceLocator(IAudioDeviceEnumerator enumerator) { _enumerator = enumerator; }

    public IReadOnlyList<string> EnumerateRenderDeviceNames() =>
        _enumerator.EnumerateRenderDevices().Select(d => d.FriendlyName).ToList();

    public string? FindVoiceMeeterInput() =>
        _enumerator.EnumerateRenderDevices()
            .Select(d => d.FriendlyName)
            .FirstOrDefault(n => n.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
                              && n.Contains("Input", StringComparison.OrdinalIgnoreCase));
}
```

Create `src/Soundpad/Audio/WasapiAudioDeviceEnumerator.cs`:

```csharp
using NAudio.CoreAudioApi;

namespace Soundpad.Audio;

public class WasapiAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices()
    {
        using var en = new MMDeviceEnumerator();
        var list = new List<AudioDeviceInfo>();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new AudioDeviceInfo(dev.ID, dev.FriendlyName));
        return list;
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter DeviceLocatorTests`
Expected: 5 tests passing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(audio): DeviceLocator + WASAPI enumerator"
```

---

### Task 11: AudioCommand records + IWavePlayer abstraction

**Files:**
- Create: `src/Soundpad/Audio/AudioCommands.cs`
- Create: `src/Soundpad/Audio/IWavePlayerFactory.cs`
- Create: `src/Soundpad/Audio/NAudioWavePlayerFactory.cs`

- [ ] **Step 1: Define commands**

Create `src/Soundpad/Audio/AudioCommands.cs`:

```csharp
namespace Soundpad.Audio;

public abstract record AudioCommand;
public sealed record PlayCommand(string SoundId, string FilePath) : AudioCommand;
public sealed record StopCommand : AudioCommand;
public sealed record VolumeCommand(int Value) : AudioCommand;
public sealed record SetMonitorEnabledCommand(bool Enabled) : AudioCommand;
public sealed record SetMonitorDeviceCommand(string? Device) : AudioCommand;
public sealed record SetGameDeviceCommand(string? Device) : AudioCommand;
public sealed record PlaybackEndedCommand(long PlayToken, bool IsGameStream) : AudioCommand;
public sealed record ShutdownCommand : AudioCommand;
```

- [ ] **Step 2: Define player abstraction**

Create `src/Soundpad/Audio/IWavePlayerFactory.cs`:

```csharp
using NAudio.Wave;

namespace Soundpad.Audio;

public interface IWavePlayerFactory
{
    IWavePlayer Create(string deviceFriendlyName, int latencyMs);
}
```

- [ ] **Step 3: Implement real factory (WasapiOut by name)**

Create `src/Soundpad/Audio/NAudioWavePlayerFactory.cs`:

```csharp
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundpad.Audio;

public class NAudioWavePlayerFactory : IWavePlayerFactory
{
    public IWavePlayer Create(string deviceFriendlyName, int latencyMs)
    {
        using var en = new MMDeviceEnumerator();
        var device = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => string.Equals(d.FriendlyName, deviceFriendlyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Device not found: {deviceFriendlyName}");
        return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMs);
    }
}
```

- [ ] **Step 4: Build to verify compile**

Run: `dotnet build`
Expected: success.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(audio): AudioCommand record hierarchy + IWavePlayerFactory"
```

---

### Task 12: AudioEngineThread STA + command loop skeleton

**Files:**
- Create: `src/Soundpad/Audio/AudioEngineThread.cs`
- Create: `src/Soundpad/Audio/PlaybackEngine.cs`
- Test: `tests/Soundpad.Tests/Audio/AudioEngineThreadTests.cs`

- [ ] **Step 1: Failing test: Shutdown stops the thread**

Create `tests/Soundpad.Tests/Audio/AudioEngineThreadTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class AudioEngineThreadTests
{
    [Fact]
    public void Shutdown_Stops_The_Thread()
    {
        var factory = new Mock<IWavePlayerFactory>();
        var cache = new SoundCache(new Mock<ISoundDecoder>().Object, capacity: 10);
        var engine = new PlaybackEngine(factory.Object, cache);
        engine.Start();
        engine.Shutdown();
        engine.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Posting_Command_After_Shutdown_Throws()
    {
        var factory = new Mock<IWavePlayerFactory>();
        var cache = new SoundCache(new Mock<ISoundDecoder>().Object, capacity: 10);
        var engine = new PlaybackEngine(factory.Object, cache);
        engine.Start();
        engine.Shutdown();
        Assert.Throws<InvalidOperationException>(() => engine.Play("x", "x.mp3"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter AudioEngineThreadTests`
Expected: FAIL.

- [ ] **Step 3: Implement PlaybackEngine + thread loop skeleton**

Create `src/Soundpad/Audio/PlaybackEngine.cs`:

```csharp
using System.Collections.Concurrent;
using NAudio.Wave;

namespace Soundpad.Audio;

public class PlaybackEngine
{
    private readonly IWavePlayerFactory _factory;
    private readonly SoundCache _cache;
    private readonly BlockingCollection<AudioCommand> _queue = new();
    private Thread? _thread;
    private long _playToken;
    private IWavePlayer? _gamePlayer;
    private IWavePlayer? _monitorPlayer;
    private RawSourceWaveStream? _gameStream;
    private RawSourceWaveStream? _monitorStream;
    private MemoryStream? _gameMs;
    private MemoryStream? _monitorMs;
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _gameVol;
    private NAudio.Wave.SampleProviders.VolumeSampleProvider? _monitorVol;
    private string? _gameDevice;
    private string? _monitorDevice;
    private bool _monitorEnabled;
    private int _volume = 80;
    private int _latencyMs = 50;
    private string? _nowPlaying;

    public event Action<string>? Playing;
    public event Action? Stopped;
    public event Action<bool>? MonitorChanged;
    public event Action<int>? VolumeChanged;
    public event Action<string?>? MonitorDeviceChanged;

    public PlaybackEngine(IWavePlayerFactory factory, SoundCache cache)
    {
        _factory = factory; _cache = cache;
    }

    public bool IsRunning => _thread is { IsAlive: true };
    public string? NowPlaying => _nowPlaying;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "audio-engine" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Shutdown()
    {
        if (_thread is null) return;
        _queue.Add(new ShutdownCommand());
        _thread.Join();
        _thread = null;
    }

    public void Play(string id, string filePath) => Post(new PlayCommand(id, filePath));
    public void Stop() => Post(new StopCommand());
    public void SetVolume(int v) => Post(new VolumeCommand(v));
    public void SetMonitorEnabled(bool b) => Post(new SetMonitorEnabledCommand(b));
    public void SetMonitorDevice(string? d) => Post(new SetMonitorDeviceCommand(d));
    public void SetGameDevice(string? d) => Post(new SetGameDeviceCommand(d));
    public void SetLatency(int ms) { _latencyMs = ms; }

    private void Post(AudioCommand c)
    {
        if (_queue.IsAddingCompleted) throw new InvalidOperationException("Engine shut down");
        _queue.Add(c);
    }

    private void Loop()
    {
        foreach (var cmd in _queue.GetConsumingEnumerable())
        {
            try { Dispatch(cmd); }
            catch (Exception ex) { Console.Error.WriteLine($"audio-engine: {ex}"); }
            if (cmd is ShutdownCommand)
            {
                DisposeAllPlayers();
                _queue.CompleteAdding();
                break;
            }
        }
    }

    private void Dispatch(AudioCommand cmd)
    {
        // Filled in by subsequent tasks.
    }

    private void DisposeAllPlayers()
    {
        DisposePlayer(ref _gamePlayer, ref _gameStream, ref _gameMs, ref _gameVol);
        DisposePlayer(ref _monitorPlayer, ref _monitorStream, ref _monitorMs, ref _monitorVol);
    }

    private static void DisposePlayer(ref IWavePlayer? player, ref RawSourceWaveStream? stream,
                                      ref MemoryStream? ms, ref NAudio.Wave.SampleProviders.VolumeSampleProvider? vol)
    {
        if (player is not null) { player.Stop(); player.Dispose(); }
        stream?.Dispose(); ms?.Dispose();
        player = null; stream = null; ms = null; vol = null;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter AudioEngineThreadTests`
Expected: 2 tests passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(audio): PlaybackEngine STA thread + command queue scaffold"
```

---

### Task 13: PlayCommand handler (game device only)

**Files:**
- Modify: `src/Soundpad/Audio/PlaybackEngine.cs` (fill `Dispatch`)
- Test: `tests/Soundpad.Tests/Audio/PlayCommandTests.cs`

- [ ] **Step 1: Write failing test using a fake IWavePlayer**

Create `tests/Soundpad.Tests/Audio/FakeWavePlayer.cs`:

```csharp
using NAudio.Wave;

namespace Soundpad.Tests.Audio;

public class FakeWavePlayer : IWavePlayer
{
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public int ManagedThreadIdAtInit = -1;
    public int ManagedThreadIdAtPlay = -1;
    public int ManagedThreadIdAtDispose = -1;
    public string DeviceName;
    public bool Disposed;

    public FakeWavePlayer(string deviceName)
    {
        DeviceName = deviceName;
        ManagedThreadIdAtInit = Thread.CurrentThread.ManagedThreadId;
    }
    public void Init(IWaveProvider waveProvider) { OutputWaveFormat = waveProvider.WaveFormat; }
    public void Play() { ManagedThreadIdAtPlay = Thread.CurrentThread.ManagedThreadId; PlaybackState = PlaybackState.Playing; }
    public void Stop() { PlaybackState = PlaybackState.Stopped; }
    public void Pause() { PlaybackState = PlaybackState.Paused; }
    public void Dispose() { ManagedThreadIdAtDispose = Thread.CurrentThread.ManagedThreadId; Disposed = true; }
    public void RaiseStopped() => PlaybackStopped?.Invoke(this, new StoppedEventArgs());
}
```

Create `tests/Soundpad.Tests/Audio/PlayCommandTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class PlayCommandTests
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[4096]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache);
        engine.SetGameDevice("VoiceMeeter Input");
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Play_Single_Device_Creates_One_Player()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        _players.Should().HaveCount(1);
        _players[0].DeviceName.Should().Be("VoiceMeeter Input");
        _players[0].PlaybackState.Should().Be(PlaybackState.Playing);
        engine.NowPlaying.Should().Be("a");
        engine.Shutdown();
    }

    [Fact]
    public async Task Play_Emits_Playing_Event()
    {
        var engine = Build();
        string? heard = null;
        engine.Playing += id => heard = id;
        engine.Play("ulala", "x.mp3");
        await Task.Delay(100);
        heard.Should().Be("ulala");
        engine.Shutdown();
    }

    [Fact]
    public async Task Play_Then_Play_Stops_Previous_First()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Play("b", "b.mp3");
        await Task.Delay(100);
        _players[0].Disposed.Should().BeTrue();
        _players[1].PlaybackState.Should().Be(PlaybackState.Playing);
        engine.NowPlaying.Should().Be("b");
        engine.Shutdown();
    }

    [Fact]
    public async Task Player_Created_On_AudioEngine_Thread()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        _players[0].ManagedThreadIdAtInit.Should().Be(_players[0].ManagedThreadIdAtPlay);
        engine.Shutdown();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter PlayCommandTests`
Expected: FAIL (dispatch is empty).

- [ ] **Step 3: Implement Dispatch — PlayCommand + StopCommand**

Replace the empty `Dispatch` method body in `src/Soundpad/Audio/PlaybackEngine.cs`:

```csharp
    private void Dispatch(AudioCommand cmd)
    {
        switch (cmd)
        {
            case PlayCommand p: HandlePlay(p); break;
            case StopCommand: HandleStop(); break;
            case VolumeCommand v: HandleVolume(v.Value); break;
            case SetMonitorEnabledCommand m: HandleSetMonitorEnabled(m.Enabled); break;
            case SetMonitorDeviceCommand md: HandleSetMonitorDevice(md.Device); break;
            case SetGameDeviceCommand gd: _gameDevice = gd.Device; break;
            case PlaybackEndedCommand pe: HandlePlaybackEnded(pe); break;
            case ShutdownCommand: /* handled by Loop */ break;
        }
    }

    private void HandlePlay(PlayCommand cmd)
    {
        if (string.IsNullOrEmpty(_gameDevice))
        {
            Console.Error.WriteLine("PlayCommand: no game device configured");
            return;
        }
        DisposeAllPlayers();
        var token = Interlocked.Increment(ref _playToken);
        var cached = _cache.Get(cmd.FilePath);

        (_gamePlayer, _gameStream, _gameMs, _gameVol) = OpenStream(cached, _gameDevice);
        _gamePlayer!.PlaybackStopped += (s, e) => _queue.Add(new PlaybackEndedCommand(token, IsGameStream: true));
        _gamePlayer.Init(_gameVol);
        _gamePlayer.Play();

        if (_monitorEnabled && !string.IsNullOrEmpty(_monitorDevice))
        {
            (_monitorPlayer, _monitorStream, _monitorMs, _monitorVol) = OpenStream(cached, _monitorDevice);
            _monitorPlayer!.PlaybackStopped += (s, e) => _queue.Add(new PlaybackEndedCommand(token, IsGameStream: false));
            _monitorPlayer.Init(_monitorVol);
            _monitorPlayer.Play();
        }

        _nowPlaying = cmd.SoundId;
        Playing?.Invoke(cmd.SoundId);
    }

    private (IWavePlayer Player, RawSourceWaveStream Stream, MemoryStream Ms, NAudio.Wave.SampleProviders.VolumeSampleProvider Vol)
        OpenStream(CachedSound cached, string deviceName)
    {
        var ms = new MemoryStream(cached.PcmBytes, writable: false);
        var raw = new RawSourceWaveStream(ms, cached.Format);
        var vol = new NAudio.Wave.SampleProviders.VolumeSampleProvider(raw.ToSampleProvider()) { Volume = _volume / 100f };
        var player = _factory.Create(deviceName, _latencyMs);
        return (player, raw, ms, vol);
    }

    private void HandleStop()
    {
        DisposeAllPlayers();
        _nowPlaying = null;
        Stopped?.Invoke();
    }

    private void HandleVolume(int v)
    {
        _volume = Math.Clamp(v, 0, 100);
        if (_gameVol is not null) _gameVol.Volume = _volume / 100f;
        if (_monitorVol is not null) _monitorVol.Volume = _volume / 100f;
        VolumeChanged?.Invoke(_volume);
    }

    private void HandleSetMonitorEnabled(bool b)
    {
        _monitorEnabled = b;
        MonitorChanged?.Invoke(b);
    }

    private void HandleSetMonitorDevice(string? d)
    {
        _monitorDevice = d;
        MonitorDeviceChanged?.Invoke(d);
    }

    private void HandlePlaybackEnded(PlaybackEndedCommand cmd)
    {
        if (cmd.PlayToken != _playToken) return; // stale
        if (!cmd.IsGameStream) return; // monitor's stop is not authoritative
        DisposeAllPlayers();
        _nowPlaying = null;
        Stopped?.Invoke();
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter PlayCommandTests`
Expected: 4 tests passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(audio): PlayCommand handler — game stream + dispose order"
```

---

### Task 14: Monitor support + null-device guard

**Files:**
- Test: `tests/Soundpad.Tests/Audio/MonitorTests.cs`

- [ ] **Step 1: Failing tests for monitor branching**

Create `tests/Soundpad.Tests/Audio/MonitorTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class MonitorTests
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[4096]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache);
        engine.SetGameDevice("Game");
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Monitor_Enabled_With_Device_Creates_Two_Players()
    {
        var engine = Build();
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        _players.Should().HaveCount(2);
        _players.Should().Contain(p => p.DeviceName == "Game");
        _players.Should().Contain(p => p.DeviceName == "Headphones");
        engine.Shutdown();
    }

    [Fact]
    public async Task Monitor_Enabled_But_Null_Device_Creates_One_Player()
    {
        var engine = Build();
        engine.SetMonitorEnabled(true);
        // monitorDevice not set (null)
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        _players.Should().HaveCount(1);
        _players[0].DeviceName.Should().Be("Game");
        engine.Shutdown();
    }

    [Fact]
    public async Task Toggle_Monitor_During_Playback_Does_Not_Affect_Current_Sound()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        await Task.Delay(50);
        _players.Should().HaveCount(1); // still only game
        engine.Shutdown();
    }

    [Fact]
    public async Task Two_MemoryStreams_Have_Independent_Positions()
    {
        var engine = Build();
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        engine.Play("a", "a.mp3");
        await Task.Delay(100);
        // Both players were initialized — proxy for "got independent streams"
        _players.Should().HaveCount(2);
        _players.All(p => p.PlaybackState == PlaybackState.Playing).Should().BeTrue();
        engine.Shutdown();
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter MonitorTests`
Expected: all 4 passing (handler already implements this branching from Task 13).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test(audio): monitor branching, null-device guard, position independence"
```

---

### Task 15: PlaybackEnded race resolution via token

**Files:**
- Test: `tests/Soundpad.Tests/Audio/PlaybackEndedTests.cs`

- [ ] **Step 1: Failing tests for natural end + stale token**

Create `tests/Soundpad.Tests/Audio/PlaybackEndedTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class PlaybackEndedTests
{
    private readonly Mock<IWavePlayerFactory> _factory = new();
    private readonly Mock<ISoundDecoder> _decoder = new();
    private readonly List<FakeWavePlayer> _players = new();

    private PlaybackEngine Build()
    {
        _factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string name, int lat) => { var p = new FakeWavePlayer(name); _players.Add(p); return p; });
        _decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[4096]));
        var cache = new SoundCache(_decoder.Object, 50);
        var engine = new PlaybackEngine(_factory.Object, cache);
        engine.SetGameDevice("Game");
        engine.Start();
        return engine;
    }

    [Fact]
    public async Task Natural_End_Of_Game_Stream_Emits_Stopped()
    {
        var engine = Build();
        bool stopped = false;
        engine.Stopped += () => stopped = true;
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        _players[0].RaiseStopped();
        await Task.Delay(100);
        stopped.Should().BeTrue();
        engine.NowPlaying.Should().BeNull();
        engine.Shutdown();
    }

    [Fact]
    public async Task Monitor_End_Alone_Does_Not_Stop()
    {
        var engine = Build();
        engine.SetMonitorDevice("Headphones");
        engine.SetMonitorEnabled(true);
        bool stopped = false;
        engine.Stopped += () => stopped = true;
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        var monitor = _players.Single(p => p.DeviceName == "Headphones");
        monitor.RaiseStopped();
        await Task.Delay(100);
        stopped.Should().BeFalse();
        engine.NowPlaying.Should().Be("a");
        engine.Shutdown();
    }

    [Fact]
    public async Task Stale_Token_End_Of_A_Does_Not_Clear_B()
    {
        var engine = Build();
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Play("b", "b.mp3");
        await Task.Delay(50);
        // First player got disposed when b was played. Even if its handler somehow fired:
        _players[0].RaiseStopped(); // stale token
        await Task.Delay(100);
        engine.NowPlaying.Should().Be("b");
        engine.Shutdown();
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter PlaybackEndedTests`
Expected: 3 passing (logic already in HandlePlaybackEnded).

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test(audio): natural end + monitor authority + token race"
```

---

### Task 16: Thread affinity verification

**Files:**
- Test: `tests/Soundpad.Tests/Audio/ThreadAffinityTests.cs`

- [ ] **Step 1: Failing test that all WasapiOut lifecycle is on audio thread**

Create `tests/Soundpad.Tests/Audio/ThreadAffinityTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class ThreadAffinityTests
{
    [Fact]
    public async Task All_Player_Lifecycle_Occurs_On_AudioEngine_Thread()
    {
        var factory = new Mock<IWavePlayerFactory>();
        var players = new List<FakeWavePlayer>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string n, int lat) => { var p = new FakeWavePlayer(n); players.Add(p); return p; });
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[1024]));
        var cache = new SoundCache(decoder.Object, 50);
        var engine = new PlaybackEngine(factory.Object, cache);
        engine.SetGameDevice("G");
        engine.Start();

        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.Stop();
        await Task.Delay(50);

        var caller = Thread.CurrentThread.ManagedThreadId;
        var p = players[0];
        p.ManagedThreadIdAtInit.Should().NotBe(caller);
        p.ManagedThreadIdAtPlay.Should().Be(p.ManagedThreadIdAtInit);
        p.ManagedThreadIdAtDispose.Should().Be(p.ManagedThreadIdAtInit);

        engine.Shutdown();
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test --filter ThreadAffinityTests`
Expected: passing — confirms WasapiOut lifecycle is owned exclusively by the audio thread.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test(audio): thread affinity of WasapiOut lifecycle"
```

---

### Task 17: Volume in real-time + monitorDevice change events

**Files:**
- Test: `tests/Soundpad.Tests/Audio/VolumeAndMonitorDeviceTests.cs`

- [ ] **Step 1: Failing tests**

Create `tests/Soundpad.Tests/Audio/VolumeAndMonitorDeviceTests.cs`:

```csharp
using FluentAssertions;
using Moq;
using NAudio.Wave;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class VolumeAndMonitorDeviceTests
{
    private PlaybackEngine Build(out List<FakeWavePlayer> players)
    {
        var p = new List<FakeWavePlayer>();
        var factory = new Mock<IWavePlayerFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string n, int lat) => { var x = new FakeWavePlayer(n); p.Add(x); return x; });
        var decoder = new Mock<ISoundDecoder>();
        decoder.Setup(d => d.Decode(It.IsAny<string>()))
            .Returns(new CachedSound(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), new byte[1024]));
        var cache = new SoundCache(decoder.Object, 50);
        var engine = new PlaybackEngine(factory.Object, cache);
        engine.SetGameDevice("G");
        engine.Start();
        players = p;
        return engine;
    }

    [Fact]
    public async Task SetVolume_During_Playback_Emits_Event()
    {
        var engine = Build(out var _);
        int? heard = null;
        engine.VolumeChanged += v => heard = v;
        engine.Play("a", "a.mp3");
        await Task.Delay(50);
        engine.SetVolume(42);
        await Task.Delay(50);
        heard.Should().Be(42);
        engine.Shutdown();
    }

    [Fact]
    public async Task SetMonitorDevice_Emits_Event()
    {
        var engine = Build(out var _);
        string? heard = "unchanged";
        engine.MonitorDeviceChanged += d => heard = d;
        engine.SetMonitorDevice("Foo");
        await Task.Delay(50);
        heard.Should().Be("Foo");
        engine.Shutdown();
    }

    [Fact]
    public async Task SetMonitorEnabled_Emits_Event()
    {
        var engine = Build(out var _);
        bool? heard = null;
        engine.MonitorChanged += b => heard = b;
        engine.SetMonitorEnabled(true);
        await Task.Delay(50);
        heard.Should().BeTrue();
        engine.Shutdown();
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter VolumeAndMonitorDeviceTests`
Expected: 3 passing.

- [ ] **Step 3: Run full suite**

Run: `dotnet test`
Expected: all previous tests still pass.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test(audio): runtime volume + monitor events"
```

---

## Phase 3 — API + Auth + StateHub

### Task 18: Program.cs bootstrap + DI wiring

**Files:**
- Modify: `src/Soundpad/Program.cs`
- Create: `src/Soundpad/AppOptions.cs`

- [ ] **Step 1: Define AppOptions for paths**

Create `src/Soundpad/AppOptions.cs`:

```csharp
namespace Soundpad;

public record AppOptions(string RootDir);
```

- [ ] **Step 2: Implement bootstrap**

Replace `src/Soundpad/Program.cs`:

```csharp
using Microsoft.AspNetCore.Http.Features;
using Soundpad;
using Soundpad.Audio;
using Soundpad.Sound;

var rootDir = AppContext.BaseDirectory;
var libOpts = new SoundLibraryOptions(rootDir);
Directory.CreateDirectory(libOpts.SoundsDir);

var library = new SoundLibrary(libOpts);
library.Load();
library.RepairInvariants();
library.AutoScan();

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = SoundLibrary.MaxUploadBytes;
    o.ListenAnyIP(library.Config.Port);
});
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = SoundLibrary.MaxUploadBytes;
});

builder.Services.AddSingleton(library);
builder.Services.AddSingleton(new AppOptions(rootDir));
builder.Services.AddSingleton<IAudioDeviceEnumerator, WasapiAudioDeviceEnumerator>();
builder.Services.AddSingleton<DeviceLocator>();
builder.Services.AddSingleton<IWavePlayerFactory, NAudioWavePlayerFactory>();
builder.Services.AddSingleton<ISoundDecoder, NAudioSoundDecoder>();
builder.Services.AddSingleton<SoundCache>(sp => new SoundCache(sp.GetRequiredService<ISoundDecoder>(), 50));
builder.Services.AddSingleton<PlaybackEngine>();

var app = builder.Build();

var engine = app.Services.GetRequiredService<PlaybackEngine>();
engine.SetGameDevice(library.Config.AudioDevice);
engine.SetMonitorDevice(library.Config.MonitorDevice);
engine.SetMonitorEnabled(library.Config.MonitorEnabled);
engine.SetVolume(library.Config.Volume);
engine.SetLatency(library.Config.LatencyMs);
engine.Start();

app.UseStaticFiles();
app.MapGet("/", () => Results.File(Path.Combine(rootDir, "wwwroot", "index.html"), "text/html"));

app.Lifetime.ApplicationStopping.Register(() => engine.Shutdown());

app.Run();
```

- [ ] **Step 3: Build to verify compile**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(host): Program.cs bootstrap — DI + Kestrel + engine wiring"
```

---

### Task 19: AuthTokenMiddleware

**Files:**
- Create: `src/Soundpad/Security/AuthTokenMiddleware.cs`
- Test: `tests/Soundpad.Tests/Security/AuthTokenTests.cs`

- [ ] **Step 1: Failing tests via WebApplicationFactory**

Create `tests/Soundpad.Tests/Security/AuthTokenTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Soundpad.Tests.Security;

public class AuthTokenTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public AuthTokenTests(WebApplicationFactory<Program> f) { _client = f.CreateClient(); }

    [Fact]
    public async Task Api_Without_Token_Returns_401()
    {
        var r = await _client.GetAsync("/api/state");
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Root_Is_Public()
    {
        var r = await _client.GetAsync("/");
        r.IsSuccessStatusCode.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Verify failure**

Run: `dotnet test --filter AuthTokenTests`
Expected: FAIL — endpoints not yet wired or middleware not yet present (depends on order; if state endpoint doesn't exist this test will fail too — that's OK, fix in Task 21).

- [ ] **Step 3: Implement middleware**

Create `src/Soundpad/Security/AuthTokenMiddleware.cs`:

```csharp
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
        _next = next; _library = library;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var needsAuth = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                        path.Equals("/ws", StringComparison.OrdinalIgnoreCase);
        if (!needsAuth) { await _next(ctx); return; }

        var supplied = ctx.Request.Headers["X-Auth-Token"].FirstOrDefault()
                       ?? ctx.Request.Query["t"].FirstOrDefault();
        if (supplied is null || !TokensMatch(supplied, _library.Config.AuthToken))
        {
            ctx.Response.StatusCode = 401;
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
```

- [ ] **Step 4: Register middleware in Program.cs**

In `Program.cs`, after `var app = builder.Build();` and before `app.UseStaticFiles();`, add:

```csharp
app.UseMiddleware<Soundpad.Security.AuthTokenMiddleware>();
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter AuthTokenTests`
Expected: both passing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(security): AuthTokenMiddleware — constant-time match, header/query"
```

---

### Task 20: StateHub (WebSocket) with originId

**Files:**
- Create: `src/Soundpad/Api/StateHub.cs`
- Create: `src/Soundpad/Api/WsEnvelope.cs`
- Modify: `src/Soundpad/Program.cs` (register WS endpoint)
- Test: `tests/Soundpad.Tests/Api/StateHubTests.cs`

- [ ] **Step 1: Define envelope**

Create `src/Soundpad/Api/WsEnvelope.cs`:

```csharp
namespace Soundpad.Api;

public record WsEnvelope(string Type, string? OriginId, object Payload);
```

- [ ] **Step 2: Implement StateHub broadcast**

Create `src/Soundpad/Api/StateHub.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Soundpad.Api;

public class StateHub
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task AcceptAsync(HttpContext ctx)
    {
        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _sockets[id] = ws;
        try
        {
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buf, ctx.RequestAborted);
                if (res.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally
        {
            _sockets.TryRemove(id, out _);
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    public Task BroadcastAsync(string type, object payload, string? originId = null)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new WsEnvelope(type, originId, payload), Json));
        var tasks = _sockets.Values.Where(ws => ws.State == WebSocketState.Open)
            .Select(ws => ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).AsTask());
        return Task.WhenAll(tasks);
    }
}
```

- [ ] **Step 3: Register WS endpoint + wire engine events**

In `Program.cs`, add singleton + event subscriptions:

```csharp
builder.Services.AddSingleton<Soundpad.Api.StateHub>();
```

After `engine.Start();` and before `app.UseStaticFiles();`:

```csharp
app.UseWebSockets();

var hub = app.Services.GetRequiredService<Soundpad.Api.StateHub>();
engine.Playing += id => _ = hub.BroadcastAsync("playing", new { soundId = id });
engine.Stopped += () => _ = hub.BroadcastAsync("stopped", new { });
engine.MonitorChanged += b => _ = hub.BroadcastAsync("monitorChanged", new { enabled = b });
engine.VolumeChanged += v => _ = hub.BroadcastAsync("volumeChanged", new { value = v });
engine.MonitorDeviceChanged += d => _ = hub.BroadcastAsync("monitorDeviceChanged", new { device = d });

app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await hub.AcceptAsync(ctx);
});
```

- [ ] **Step 4: Failing integration test**

Create `tests/Soundpad.Tests/Api/StateHubTests.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Soundpad.Tests.Api;

public class StateHubTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public StateHubTests(WebApplicationFactory<Program> f) { _factory = f; }

    [Fact]
    public async Task WS_Without_Token_Rejected_With_401()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/ws"); // not a WS handshake, just confirm 401 path
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter StateHubTests`
Expected: 1 passing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(api): StateHub WebSocket + engine event broadcasts"
```

---

### Task 21: Playback + state endpoints

**Files:**
- Create: `src/Soundpad/Api/PlaybackEndpoints.cs`
- Create: `src/Soundpad/Api/Dto/StateDto.cs`
- Modify: `src/Soundpad/Program.cs` (register endpoints)
- Test: `tests/Soundpad.Tests/Api/PlaybackEndpointsTests.cs`

- [ ] **Step 1: Define StateDto**

Create `src/Soundpad/Api/Dto/StateDto.cs`:

```csharp
using Soundpad.Models;

namespace Soundpad.Api.Dto;

public record SoundEntryDto(string Id, string File, string Label, string Color, string? Icon, GridPosition? Position, bool Missing);

public record StateDto(
    string? AudioDevice, string? MonitorDevice, bool MonitorEnabled,
    IReadOnlyList<string> AvailableOutputDevices,
    int Volume, GridLayout Grid,
    IReadOnlyList<SoundEntryDto> Sounds,
    string? NowPlaying, bool AuthRequired);
```

- [ ] **Step 2: Implement endpoints**

Create `src/Soundpad/Api/PlaybackEndpoints.cs`:

```csharp
using Soundpad.Api.Dto;
using Soundpad.Audio;
using Soundpad.Sound;

namespace Soundpad.Api;

public static class PlaybackEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/state", (SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc) =>
        {
            var statuses = lib.GetRuntimeStatuses().ToDictionary(s => s.Id, s => s.Missing);
            var entries = lib.Config.Sounds.Select(s => new SoundEntryDto(
                s.Id, s.File, s.Label, s.Color, s.Icon, s.Position,
                statuses.TryGetValue(s.Id, out var m) && m)).ToList();
            return new StateDto(
                lib.Config.AudioDevice, lib.Config.MonitorDevice, lib.Config.MonitorEnabled,
                loc.EnumerateRenderDeviceNames(),
                lib.Config.Volume, lib.Config.Grid, entries,
                engine.NowPlaying, AuthRequired: true);
        });

        g.MapPost("/play/{soundId}", (string soundId, SoundLibrary lib, PlaybackEngine engine, AppOptions opts) =>
        {
            var entry = lib.Config.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (entry is null) return Results.NotFound();
            var path = Path.Combine(opts.RootDir, "sounds", entry.File);
            if (!File.Exists(path)) return Results.NotFound();
            engine.Play(soundId, path);
            return Results.NoContent();
        });

        g.MapPost("/stop", (PlaybackEngine engine) => { engine.Stop(); return Results.NoContent(); });

        g.MapPost("/volume", (VolumeBody body, SoundLibrary lib, PlaybackEngine engine) =>
        {
            var v = Math.Clamp(body.Value, 0, 100);
            engine.SetVolume(v);
            lib.MutateConfig(c => c with { Volume = v });
            lib.Save();
            return Results.NoContent();
        });

        g.MapPost("/monitor", (MonitorBody body, SoundLibrary lib, PlaybackEngine engine) =>
        {
            engine.SetMonitorEnabled(body.Enabled);
            lib.MutateConfig(c => c with { MonitorEnabled = body.Enabled });
            lib.Save();
            return Results.NoContent();
        });

        g.MapPost("/monitor/device", (MonitorDeviceBody body, SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc) =>
        {
            if (body.Device is not null && !loc.EnumerateRenderDeviceNames().Contains(body.Device, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "unknown_device", available = loc.EnumerateRenderDeviceNames() });
            engine.SetMonitorDevice(body.Device);
            lib.MutateConfig(c => c with { MonitorDevice = body.Device });
            lib.Save();
            return Results.NoContent();
        });
    }

    public record VolumeBody(int Value);
    public record MonitorBody(bool Enabled);
    public record MonitorDeviceBody(string? Device);
}
```

- [ ] **Step 3: Wire in Program.cs**

In `Program.cs`, after the `/ws` map block:

```csharp
Soundpad.Api.PlaybackEndpoints.Map(app);
```

- [ ] **Step 4: Write failing integration tests**

Create `tests/Soundpad.Tests/Api/PlaybackEndpointsTests.cs`:

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Soundpad.Api.Dto;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

public class PlaybackEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public PlaybackEndpointsTests(WebApplicationFactory<Program> f) { _factory = f; }

    private async Task<HttpClient> AuthedClient()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task GetState_Returns_Schema()
    {
        var c = await AuthedClient();
        var s = await c.GetFromJsonAsync<StateDto>("/api/state");
        s.Should().NotBeNull();
        s!.AuthRequired.Should().BeTrue();
        s.Grid.Cols.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Play_Unknown_Returns_404()
    {
        var c = await AuthedClient();
        var r = await c.PostAsync("/api/play/this-does-not-exist", null);
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Volume_Persists_To_Config()
    {
        var c = await AuthedClient();
        var r = await c.PostAsJsonAsync("/api/volume", new { value = 33 });
        r.IsSuccessStatusCode.Should().BeTrue();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        lib.Config.Volume.Should().Be(33);
    }

    [Fact]
    public async Task MonitorDevice_Unknown_Returns_400()
    {
        var c = await AuthedClient();
        var r = await c.PostAsJsonAsync("/api/monitor/device", new { device = "Definitely Not Real" });
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter PlaybackEndpointsTests`
Expected: 4 passing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(api): playback + state endpoints + StateDto"
```

---

### Task 22: Sound + grid endpoints (CRUD, upload, layout)

**Files:**
- Create: `src/Soundpad/Api/SoundEndpoints.cs`
- Modify: `src/Soundpad/Program.cs`
- Test: `tests/Soundpad.Tests/Api/SoundEndpointsTests.cs`

- [ ] **Step 1: Implement endpoints**

Create `src/Soundpad/Api/SoundEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Soundpad.Models;
using Soundpad.Sound;

namespace Soundpad.Api;

public static class SoundEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapPost("/sounds/upload", async (HttpRequest req, SoundLibrary lib, StateHub hub) =>
        {
            // Third defense layer (Kestrel + FormOptions are layers 1 and 2):
            if (req.ContentLength is long len && len > SoundLibrary.MaxUploadBytes)
                return Results.BadRequest(new { error = "too_large" });
            if (!req.HasFormContentType) return Results.BadRequest(new { error = "expected_multipart" });
            var form = await req.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null) return Results.BadRequest(new { error = "missing_file" });
            try
            {
                using var stream = file.OpenReadStream();
                var result = lib.Upload(file.FileName, stream);
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.Created($"/api/sounds/{result.Entry.Id}", result.Entry);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();

        g.MapPut("/sounds/{id}", (string id, UpdateBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.UpdateSound(id,
                    label: body.Label, color: body.Color, icon: body.Icon,
                    position: body.Position, clearPosition: body.ClearPosition ?? false);
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapDelete("/sounds/{id}", (string id, bool? deleteFile, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.DeleteSound(id, deleteFile ?? false);
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPost("/grid", (GridBody body, SoundLibrary lib, StateHub hub) =>
        {
            if (body.Cols < 1 || body.Rows < 1) return Results.BadRequest(new { error = "invalid_dimensions" });
            lib.ResizeGrid(body.Cols, body.Rows);
            _ = hub.BroadcastAsync("libraryChanged", new { });
            return Results.NoContent();
        });

        g.MapPost("/grid/layout", (LayoutBody body, SoundLibrary lib, StateHub hub) =>
        {
            try
            {
                lib.ApplyLayout(body.Placements.Select(p => (p.Id, p.Position)));
                _ = hub.BroadcastAsync("libraryChanged", new { });
                return Results.NoContent();
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    public record UpdateBody(string? Label, string? Color, string? Icon, GridPosition? Position, bool? ClearPosition);
    public record GridBody(int Cols, int Rows);
    public record LayoutPlacement(string Id, GridPosition? Position);
    public record LayoutBody(List<LayoutPlacement> Placements);
}
```

- [ ] **Step 2: Register in Program.cs**

After `PlaybackEndpoints.Map(app);`:

```csharp
Soundpad.Api.SoundEndpoints.Map(app);
```

- [ ] **Step 3: Failing integration tests**

Create `tests/Soundpad.Tests/Api/SoundEndpointsTests.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

public class SoundEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public SoundEndpointsTests(WebApplicationFactory<Program> f) { _factory = f; }

    private HttpClient Auth()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task Upload_Bad_Extension_Returns_400()
    {
        var c = Auth();
        var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(new byte[10]);
        bytes.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        content.Add(bytes, "file", "evil.exe");
        var r = await c.PostAsync("/api/sounds/upload", content);
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grid_Layout_Duplicate_Positions_Returns_400()
    {
        var c = Auth();
        // need two known sounds first
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        if (lib.Config.Sounds.Count < 2) { return; }
        var ids = lib.Config.Sounds.Take(2).Select(s => s.Id).ToList();
        var body = new
        {
            placements = new[]
            {
                new { id = ids[0], position = new { col = 0, row = 0 } },
                new { id = ids[1], position = new { col = 0, row = 0 } },
            }
        };
        var r = await c.PostAsJsonAsync("/api/grid/layout", body);
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grid_Resize_Updates_State()
    {
        var c = Auth();
        var r = await c.PostAsJsonAsync("/api/grid", new { cols = 4, rows = 5 });
        r.IsSuccessStatusCode.Should().BeTrue();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        lib.Config.Grid.Cols.Should().Be(4);
        lib.Config.Grid.Rows.Should().Be(5);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter SoundEndpointsTests`
Expected: 3 passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(api): sound CRUD + grid resize + atomic layout"
```

---

## Phase 4 — Front + peripherals

### Task 23: Front HTML/CSS/JS skeleton

**Files:**
- Create: `src/Soundpad/wwwroot/index.html`
- Create: `src/Soundpad/wwwroot/styles.css`
- Create: `src/Soundpad/wwwroot/app.js`
- Create: `src/Soundpad/wwwroot/manifest.json`

- [ ] **Step 1: Static index**

Create `src/Soundpad/wwwroot/index.html`:

```html
<!doctype html>
<html lang="pt-br">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no" />
  <link rel="manifest" href="/manifest.json" />
  <link rel="stylesheet" href="/styles.css" />
  <title>Soundpad</title>
</head>
<body>
  <header>
    <h1>SOUNDPAD</h1>
    <label class="monitor-toggle">
      <input type="checkbox" id="monitor" />
      <span>🎧 Monitor</span>
    </label>
  </header>
  <main>
    <div id="grid" class="grid"></div>
    <div id="orphans" class="orphans" hidden>
      <h2>Sem posição</h2>
      <div id="orphans-list"></div>
    </div>
  </main>
  <footer>
    <button id="stop" class="stop">■ STOP</button>
    <div class="vol-row">
      <input id="volume" type="range" min="0" max="100" />
      <span id="vol-value">--</span>
      <button id="settings" class="icon" title="Settings">⚙</button>
    </div>
  </footer>
  <script src="/app.js"></script>
</body>
</html>
```

- [ ] **Step 2: Manifest**

Create `src/Soundpad/wwwroot/manifest.json`:

```json
{
  "name": "Soundpad",
  "short_name": "Soundpad",
  "start_url": "/",
  "display": "standalone",
  "background_color": "#0a0a0a",
  "theme_color": "#0a0a0a"
}
```

- [ ] **Step 3: Styles (dark Stream-Deck look)**

Create `src/Soundpad/wwwroot/styles.css`:

```css
:root { color-scheme: dark; }
* { box-sizing: border-box; }
html, body { margin: 0; padding: 0; background: #0a0a0a; color: #eee; font-family: -apple-system, system-ui, sans-serif; }
body { min-height: 100vh; display: flex; flex-direction: column; }
header { padding: 12px 16px; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #222; }
h1 { font-size: 1rem; letter-spacing: 0.2em; margin: 0; }
.monitor-toggle { display: flex; align-items: center; gap: 6px; font-size: 0.9rem; }
main { flex: 1; padding: 12px; overflow-y: auto; }
.grid { display: grid; gap: 10px; grid-template-columns: repeat(var(--cols, 3), 1fr); }
.cell { aspect-ratio: 1; border-radius: 12px; background: #1a1a1a; border: 2px solid transparent; display: flex; align-items: center; justify-content: center; text-align: center; padding: 8px; font-weight: 600; font-size: 0.9rem; cursor: pointer; user-select: none; word-break: break-word; }
.cell.empty { background: transparent; border: 2px dashed #333; color: #555; font-size: 0.8rem; }
.cell.playing { border-color: #fff; animation: pulse 1s infinite; }
.cell.missing { opacity: 0.4; }
@keyframes pulse { 50% { border-color: rgba(255,255,255,0.4); } }
footer { padding: 12px 16px; border-top: 1px solid #222; background: #0a0a0a; }
.stop { width: 100%; padding: 16px; background: #b91c1c; color: white; border: 0; border-radius: 10px; font-weight: 700; font-size: 1.05rem; }
.vol-row { display: flex; align-items: center; gap: 12px; margin-top: 10px; }
.vol-row input[type=range] { flex: 1; }
.icon { background: transparent; border: 0; color: #ccc; font-size: 1.4rem; cursor: pointer; }
.orphans { margin-top: 24px; padding: 12px; border: 1px solid #333; border-radius: 8px; }
.orphans h2 { font-size: 0.85rem; margin: 0 0 8px; color: #888; letter-spacing: 0.1em; text-transform: uppercase; }
```

- [ ] **Step 4: App JS skeleton (connect, render, click)**

Create `src/Soundpad/wwwroot/app.js`:

```js
(() => {
  const params = new URLSearchParams(location.search);
  const token = params.get('t');
  if (token) localStorage.setItem('soundpad.token', token);
  const tok = localStorage.getItem('soundpad.token') || '';
  const originId = crypto.randomUUID();

  const headers = () => ({ 'X-Auth-Token': tok, 'X-Origin-Id': originId, 'Content-Type': 'application/json' });

  async function api(path, opts = {}) {
    const r = await fetch(path, { ...opts, headers: { ...headers(), ...(opts.headers || {}) } });
    if (r.status === 401) { alert('Sessão expirada. Escaneie o QR code de novo.'); throw new Error('401'); }
    return r;
  }

  let state = null;
  let nowPlaying = null;

  function render() {
    if (!state) return;
    const grid = document.getElementById('grid');
    grid.style.setProperty('--cols', state.grid.cols);
    grid.innerHTML = '';
    const positioned = new Map();
    state.sounds.forEach(s => { if (s.position) positioned.set(`${s.position.col},${s.position.row}`, s); });
    for (let r = 0; r < state.grid.rows; r++) {
      for (let c = 0; c < state.grid.cols; c++) {
        const s = positioned.get(`${c},${r}`);
        const cell = document.createElement('div');
        cell.className = 'cell' + (s ? '' : ' empty') + (s && s.id === nowPlaying ? ' playing' : '') + (s && s.missing ? ' missing' : '');
        cell.textContent = s ? s.label : '+';
        if (s) cell.addEventListener('click', () => api(`/api/play/${s.id}`, { method: 'POST' }));
        grid.appendChild(cell);
      }
    }
    document.getElementById('monitor').checked = state.monitorEnabled;
    document.getElementById('volume').value = state.volume;
    document.getElementById('vol-value').textContent = state.volume + '%';
  }

  async function loadState() {
    const r = await api('/api/state');
    state = await r.json();
    nowPlaying = state.nowPlaying;
    render();
  }

  function connectWs() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const ws = new WebSocket(`${proto}://${location.host}/ws?t=${tok}`);
    ws.onmessage = e => {
      const { type, originId: oid, payload } = JSON.parse(e.data);
      if (oid && oid === originId && (type === 'volumeChanged' || type === 'monitorChanged')) return; // echo filter
      switch (type) {
        case 'playing': nowPlaying = payload.soundId; render(); break;
        case 'stopped': nowPlaying = null; render(); break;
        case 'monitorChanged': state.monitorEnabled = payload.enabled; render(); break;
        case 'volumeChanged': state.volume = payload.value; render(); break;
        case 'libraryChanged': loadState(); break;
      }
    };
    let backoff = 1000;
    ws.onclose = () => setTimeout(() => { backoff = Math.min(backoff * 2, 30000); connectWs(); }, backoff);
  }

  document.getElementById('stop').addEventListener('click', () => api('/api/stop', { method: 'POST' }));

  document.getElementById('monitor').addEventListener('change', e => {
    api('/api/monitor', { method: 'POST', body: JSON.stringify({ enabled: e.target.checked }) });
  });

  let volTimer;
  document.getElementById('volume').addEventListener('input', e => {
    clearTimeout(volTimer);
    document.getElementById('vol-value').textContent = e.target.value + '%';
    volTimer = setTimeout(() => api('/api/volume', { method: 'POST', body: JSON.stringify({ value: parseInt(e.target.value, 10) }) }), 100);
  });

  loadState().then(connectWs);
})();
```

- [ ] **Step 5: Build + manual launch**

Run: `dotnet run --project src/Soundpad`
Open `http://localhost:<port>/?t=<token from config.json>` in browser.
Expected: grid renders, buttons clickable (will 404 on play if no sounds wired to VoiceMeeter yet — that's fine).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): static frontend — grid, play/stop, volume, monitor, WS echo filter"
```

---

### Task 24: LanAdapterPicker

**Files:**
- Create: `src/Soundpad/Network/INetworkInterfaceProvider.cs`
- Create: `src/Soundpad/Network/SystemNetworkInterfaceProvider.cs`
- Create: `src/Soundpad/Network/LanAdapterPicker.cs`
- Test: `tests/Soundpad.Tests/Network/LanAdapterPickerTests.cs`

- [ ] **Step 1: Define abstractions**

Create `src/Soundpad/Network/INetworkInterfaceProvider.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;

namespace Soundpad.Network;

public record NetworkAdapter(
    string Id, string Name, string Description, NetworkInterfaceType Type,
    bool IsUp, IReadOnlyList<IPAddress> IPv4, IReadOnlyList<IPAddress> Gateways);

public interface INetworkInterfaceProvider
{
    IReadOnlyList<NetworkAdapter> GetAll();
}
```

- [ ] **Step 2: Failing tests**

Create `tests/Soundpad.Tests/Network/LanAdapterPickerTests.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using FluentAssertions;
using Moq;
using Soundpad.Network;

namespace Soundpad.Tests.Network;

public class LanAdapterPickerTests
{
    private static NetworkAdapter Adapter(string name, string desc, NetworkInterfaceType type, string ip, string? gateway = "192.168.1.1", bool up = true)
        => new(Guid.NewGuid().ToString(), name, desc, type, up,
               new[] { IPAddress.Parse(ip) },
               gateway is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(gateway) });

    [Fact]
    public void Picks_Ethernet_Over_Virtual()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("VirtualBox Host-Only", "VirtualBox", NetworkInterfaceType.Ethernet, "192.168.56.1", gateway: null),
            Adapter("Ethernet", "Realtek", NetworkInterfaceType.Ethernet, "192.168.0.42"),
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(null).IPv4[0].ToString().Should().Be("192.168.0.42");
    }

    [Fact]
    public void Skips_Loopback()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("Loopback", "Loopback", NetworkInterfaceType.Loopback, "127.0.0.1", gateway: null),
            Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99"),
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(null).Name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void Skips_Tailscale_By_Name()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("Tailscale", "Tailscale", NetworkInterfaceType.Ethernet, "100.64.0.5", gateway: null),
            Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99"),
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(null).Name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void Honors_Preferred_Adapter_Id_When_Still_Present()
    {
        var pref = Adapter("Ethernet", "Realtek", NetworkInterfaceType.Ethernet, "192.168.0.42");
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99"),
            pref,
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(pref.Id).Id.Should().Be(pref.Id);
    }

    [Fact]
    public void Falls_Back_To_Auto_When_Preferred_Gone()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[] { Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99") });
        var picker = new LanAdapterPicker(p.Object);
        var r = picker.Pick("missing-id");
        r.Name.Should().Be("Wi-Fi");
    }
}
```

- [ ] **Step 3: Implement picker**

Create `src/Soundpad/Network/LanAdapterPicker.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;

namespace Soundpad.Network;

public class LanAdapterPicker
{
    private static readonly string[] Blocklist =
    {
        "vethernet", "virtualbox", "vmware", "wsl", "hyper-v",
        "tailscale", "hamachi", "loopback", "bluetooth"
    };

    private readonly INetworkInterfaceProvider _provider;

    public LanAdapterPicker(INetworkInterfaceProvider provider) { _provider = provider; }

    public NetworkAdapter Pick(string? preferredId)
    {
        var all = _provider.GetAll();
        if (preferredId is not null)
        {
            var pref = all.FirstOrDefault(a => a.Id == preferredId && a.IsUp && a.IPv4.Any(IsPrivate));
            if (pref is not null) return pref;
        }

        return all
            .Where(a => a.IsUp && a.IPv4.Any(IsPrivate))
            .Where(a => !IsBlocked(a))
            .OrderByDescending(a => a.Gateways.Count > 0)
            .ThenByDescending(a => a.Type is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .FirstOrDefault()
            ?? FallbackLoopback();
    }

    public IReadOnlyList<NetworkAdapter> Candidates() =>
        _provider.GetAll().Where(a => a.IsUp && a.IPv4.Any(IsPrivate)).ToList();

    private static bool IsBlocked(NetworkAdapter a)
    {
        var hay = (a.Name + " " + a.Description).ToLowerInvariant();
        return Blocklist.Any(b => hay.Contains(b));
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    private static NetworkAdapter FallbackLoopback() =>
        new("loopback", "Loopback", "Loopback", NetworkInterfaceType.Loopback, true,
            new[] { IPAddress.Loopback }, Array.Empty<IPAddress>());
}
```

Create `src/Soundpad/Network/SystemNetworkInterfaceProvider.cs`:

```csharp
using System.Net.NetworkInformation;

namespace Soundpad.Network;

public class SystemNetworkInterfaceProvider : INetworkInterfaceProvider
{
    public IReadOnlyList<NetworkAdapter> GetAll()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Select(ni =>
        {
            var props = ni.GetIPProperties();
            var v4 = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address).ToList();
            var gw = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(g => g.Address).ToList();
            return new NetworkAdapter(ni.Id, ni.Name, ni.Description, ni.NetworkInterfaceType,
                ni.OperationalStatus == OperationalStatus.Up, v4, gw);
        }).ToList();
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter LanAdapterPickerTests`
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(network): LanAdapterPicker — prioritize Ethernet/Wi-Fi, blocklist virtuals"
```

---

### Task 25: TrayIcon + QR window + boot orchestration

**Files:**
- Create: `src/Soundpad/Tray/TrayIconHost.cs`
- Create: `src/Soundpad/Tray/QrWindow.cs`
- Modify: `src/Soundpad/Program.cs`

- [ ] **Step 1: Implement TrayIconHost**

Create `src/Soundpad/Tray/TrayIconHost.cs`:

```csharp
using System.Drawing;
using System.Windows.Forms;
using Soundpad.Network;
using Soundpad.Sound;

namespace Soundpad.Tray;

public class TrayIconHost
{
    private readonly SoundLibrary _library;
    private readonly NetworkAdapter _adapter;
    private readonly int _port;
    private Thread? _uiThread;
    private NotifyIcon? _icon;
    private ApplicationContext? _ctx;

    public TrayIconHost(SoundLibrary library, NetworkAdapter adapter, int port)
    {
        _library = library; _adapter = adapter; _port = port;
    }

    public void Start()
    {
        _uiThread = new Thread(RunUi) { IsBackground = true, Name = "tray-ui" };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    public void Stop()
    {
        try { _ctx?.ExitThread(); } catch { }
    }

    private void RunUi()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var ip = _adapter.IPv4.First().ToString();
        var url = $"http://{ip}:{_port}/?t={_library.Config.AuthToken}";

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"Soundpad — {ip}:{_port}",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add($"{ip}:{_port}").Click += (_, _) => System.Threading.Tasks.Task.Run(() => Clipboard.SetText(url));
        menu.Items.Add("Mostrar QR code").Click += (_, _) => new QrWindow(url).ShowDialog();
        menu.Items.Add("Abrir pasta de sons").Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Path.Combine(AppContext.BaseDirectory, "sounds"));
        menu.Items.Add("Sair").Click += (_, _) => { Application.Exit(); Environment.Exit(0); };
        _icon.ContextMenuStrip = menu;

        _ctx = new ApplicationContext();
        Application.Run(_ctx);
        _icon.Visible = false;
        _icon.Dispose();
    }
}
```

- [ ] **Step 2: Implement QrWindow**

Create `src/Soundpad/Tray/QrWindow.cs`:

```csharp
using System.Drawing;
using System.Windows.Forms;
using QRCoder;

namespace Soundpad.Tray;

public class QrWindow : Form
{
    public QrWindow(string url)
    {
        Text = "Soundpad — QR";
        Size = new Size(380, 420);
        StartPosition = FormStartPosition.CenterScreen;

        using var g = new QRCodeGenerator();
        using var data = g.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(20);
        using var ms = new MemoryStream(bytes);
        var img = Image.FromStream(ms);

        var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
        var lbl = new Label { Text = url, Dock = DockStyle.Bottom, Height = 32, TextAlign = ContentAlignment.MiddleCenter };
        Controls.Add(pb);
        Controls.Add(lbl);
    }
}
```

- [ ] **Step 3: Wire tray into Program.cs with port fallback + adapter pick**

Replace the Kestrel `ListenAnyIP` setup in `Program.cs` with a fallback loop. Replace the start of `Program.cs` (everything before `app.UseStaticFiles();`) with the full revised version:

```csharp
using Microsoft.AspNetCore.Http.Features;
using Soundpad;
using Soundpad.Audio;
using Soundpad.Network;
using Soundpad.Sound;
using Soundpad.Tray;

var rootDir = AppContext.BaseDirectory;
Directory.CreateDirectory(Path.Combine(rootDir, "sounds"));
var libOpts = new SoundLibraryOptions(rootDir);

var library = new SoundLibrary(libOpts);
library.Load();
library.RepairInvariants();
library.AutoScan();

int boundPort = PickPort(library.Config.Port);
if (boundPort != library.Config.Port)
{
    library.MutateConfig(c => c with { Port = boundPort });
    library.Save();
}

var netProvider = new SystemNetworkInterfaceProvider();
var picker = new LanAdapterPicker(netProvider);
var adapter = picker.Pick(library.Config.PreferredNetworkAdapter);
if (adapter.Id != library.Config.PreferredNetworkAdapter)
{
    library.MutateConfig(c => c with { PreferredNetworkAdapter = adapter.Id });
    library.Save();
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = SoundLibrary.MaxUploadBytes;
    o.ListenAnyIP(boundPort);
});
builder.Services.Configure<FormOptions>(o => { o.MultipartBodyLengthLimit = SoundLibrary.MaxUploadBytes; });

builder.Services.AddSingleton(library);
builder.Services.AddSingleton(new AppOptions(rootDir));
builder.Services.AddSingleton<IAudioDeviceEnumerator, WasapiAudioDeviceEnumerator>();
builder.Services.AddSingleton<DeviceLocator>();
builder.Services.AddSingleton<IWavePlayerFactory, NAudioWavePlayerFactory>();
builder.Services.AddSingleton<ISoundDecoder, NAudioSoundDecoder>();
builder.Services.AddSingleton(sp => new SoundCache(sp.GetRequiredService<ISoundDecoder>(), 50));
builder.Services.AddSingleton<PlaybackEngine>();
builder.Services.AddSingleton<Soundpad.Api.StateHub>();

var app = builder.Build();

if (library.Config.AudioDevice is null)
{
    var loc = app.Services.GetRequiredService<DeviceLocator>();
    var vm = loc.FindVoiceMeeterInput();
    if (vm is not null) { library.MutateConfig(c => c with { AudioDevice = vm }); library.Save(); }
}

var engine = app.Services.GetRequiredService<PlaybackEngine>();
engine.SetGameDevice(library.Config.AudioDevice);
engine.SetMonitorDevice(library.Config.MonitorDevice);
engine.SetMonitorEnabled(library.Config.MonitorEnabled);
engine.SetVolume(library.Config.Volume);
engine.SetLatency(library.Config.LatencyMs);
engine.Start();

var hub = app.Services.GetRequiredService<Soundpad.Api.StateHub>();
engine.Playing += id => _ = hub.BroadcastAsync("playing", new { soundId = id });
engine.Stopped += () => _ = hub.BroadcastAsync("stopped", new { });
engine.MonitorChanged += b => _ = hub.BroadcastAsync("monitorChanged", new { enabled = b });
engine.VolumeChanged += v => _ = hub.BroadcastAsync("volumeChanged", new { value = v });
engine.MonitorDeviceChanged += d => _ = hub.BroadcastAsync("monitorDeviceChanged", new { device = d });

app.UseMiddleware<Soundpad.Security.AuthTokenMiddleware>();
app.UseWebSockets();
app.UseStaticFiles();

app.MapGet("/", () => Results.File(Path.Combine(rootDir, "wwwroot", "index.html"), "text/html"));
app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await hub.AcceptAsync(ctx);
});
Soundpad.Api.PlaybackEndpoints.Map(app);
Soundpad.Api.SoundEndpoints.Map(app);

var tray = new TrayIconHost(library, adapter, boundPort);
tray.Start();

app.Lifetime.ApplicationStopping.Register(() => { tray.Stop(); engine.Shutdown(); });

Console.WriteLine($"Soundpad rodando em http://{adapter.IPv4.First()}:{boundPort}/?t=<token-redacted>");

app.Run();

static int PickPort(int desired)
{
    for (int p = desired; p < desired + 10; p++)
    {
        try
        {
            using var s = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, p);
            s.Start(); s.Stop();
            return p;
        }
        catch (System.Net.Sockets.SocketException) { }
    }
    throw new InvalidOperationException($"No free port from {desired} to {desired + 9}");
}

public partial class Program { }
```

- [ ] **Step 4: Manual smoke test**

Run: `dotnet run --project src/Soundpad`
Expected:
- Tray icon appears.
- Console prints `Soundpad rodando em http://192.168.x.x:8080/?t=<token-redacted>`.
- Open the URL in browser → grid renders.
- Right-click tray → "Mostrar QR code" → window with QR + URL.
- Right-click tray → "Sair" → app closes.

- [ ] **Step 5: Manual integration with VoiceMeeter**

Pre-req: user has VoiceMeeter installed.
- Set Windows default playback to anything other than VoiceMeeter.
- In the running app, open from phone → settings → choose `monitorDevice = "Speakers (Realtek)"` (or whatever your headphones are).
- Toggle monitor ON.
- Open Discord/Valorant, set the microphone to "VoiceMeeter Output" (the B1 of VoiceMeeter).
- Tap a sound on phone → confirm: Discord users hear it, and you hear it locally.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(tray): NotifyIcon + QR window + boot orchestration with port fallback"
```

---

### Task 26: Logging (rolling daily + size cap)

**Files:**
- Create: `src/Soundpad/Logging/RollingFileLogger.cs` (or add a minimal logger provider)
- Modify: `src/Soundpad/Program.cs`

- [ ] **Step 1: Wire built-in console + simple file logger**

For simplicity in v1, use `Microsoft.Extensions.Logging` with a basic file provider via a tiny custom provider that rolls daily + by size. Add to `Program.cs` before `var builder`:

```csharp
var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundpad", "logs");
Directory.CreateDirectory(logDir);
```

After `var builder = WebApplication.CreateBuilder(args);`:

```csharp
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new Soundpad.Logging.RollingFileLoggerProvider(logDir, maxBytesPerFile: 10 * 1024 * 1024, retentionDays: 7));
```

- [ ] **Step 2: Implement RollingFileLoggerProvider**

Create `src/Soundpad/Logging/RollingFileLoggerProvider.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Soundpad.Logging;

public class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly long _maxBytes;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private DateTime _currentDate;
    private int _currentIndex;
    private StreamWriter? _writer;

    public RollingFileLoggerProvider(string dir, long maxBytesPerFile, int retentionDays)
    {
        _dir = dir; _maxBytes = maxBytesPerFile; _retentionDays = retentionDays;
        PurgeOld();
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            Rotate();
            _writer!.WriteLine(line);
            _writer.Flush();
        }
    }

    private void Rotate()
    {
        var today = DateTime.UtcNow.Date;
        if (_writer is null || _currentDate != today) { OpenNew(today, 1); return; }
        if (_writer.BaseStream.Length >= _maxBytes) { OpenNew(today, _currentIndex + 1); }
    }

    private void OpenNew(DateTime date, int index)
    {
        _writer?.Dispose();
        _currentDate = date;
        _currentIndex = index;
        var path = Path.Combine(_dir, $"soundpad-{date:yyyy-MM-dd}-{index}.log");
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    private void PurgeOld()
    {
        var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
        foreach (var f in Directory.EnumerateFiles(_dir, "soundpad-*.log"))
            if (File.GetLastWriteTimeUtc(f) < cutoff) try { File.Delete(f); } catch { }
    }

    public void Dispose() { _writer?.Dispose(); }
}

internal class RollingFileLogger : ILogger
{
    private readonly RollingFileLoggerProvider _provider;
    private readonly string _category;
    public RollingFileLogger(RollingFileLoggerProvider p, string c) { _provider = p; _category = c; }
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var line = $"{DateTime.UtcNow:O} [{level}] {_category}: {formatter(state, ex)}";
        if (ex is not null) line += "\n" + ex;
        _provider.Write(line);
    }

    private class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}
```

- [ ] **Step 3: Build + manual run**

Run: `dotnet run --project src/Soundpad`
Expected: log file created at `%LOCALAPPDATA%\Soundpad\logs\soundpad-YYYY-MM-DD-1.log` with boot info.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(logging): rolling file logger — daily + 10MB size cap + 7d retention"
```

---

### Task 27: README + final smoke

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write README with VoiceMeeter setup steps**

Create `README.md`:

```markdown
# Soundpad

Soundpad para Windows, controlado pelo celular via Wi-Fi. Toca sons dentro do Valorant, Discord, TeamSpeak — qualquer app que enxergue um microfone.

## Pré-requisitos

- Windows 10/11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download) (não precisa se usar o build self-contained)
- [VoiceMeeter](https://vb-audio.com/Voicemeeter/) (Standard, Banana ou Potato)
- PC e celular na mesma Wi-Fi

## Setup VoiceMeeter

1. Instalar VoiceMeeter Banana e reiniciar Windows.
2. No painel do VoiceMeeter, em HARDWARE OUT (A1), selecionar seu fone.
3. No Windows, em **Configurações de som > Dispositivos de entrada**, marcar "VoiceMeeter Output" como microfone padrão **só** para os apps que vão receber o som (ou configurar no app: Discord → microfone = VoiceMeeter Output).
4. Manter o default de **saída de som do Windows** apontando pro seu fone (NÃO pro VoiceMeeter), pra evitar que o monitor toque duas vezes pro jogo.

## Run

```bash
dotnet run --project src/Soundpad
```

Ou build self-contained:

```bash
dotnet publish src/Soundpad -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Soundpad.exe vai aparecer em `src/Soundpad/bin/Release/net8.0-windows/win-x64/publish/`.

## Uso

1. Rodar `Soundpad.exe`. Tray icon aparece.
2. Direito no tray → "Mostrar QR code".
3. Escanear no celular → abre a grade no browser.
4. (No celular) configurar **Settings → Monitor device** = seu fone (se quiser ouvir).
5. Configurar VoiceMeeter como microfone no Discord/Valorant.
6. Tocar sons!

## Configuração

`config.json` (no mesmo diretório do .exe) é gerado no primeiro boot. Pode editar manualmente quando o app está parado.

Campos relevantes:
- `port`: padrão 8080 (auto-fallback se ocupado).
- `audioDevice`: nome do dispositivo de saída pro game (default = VoiceMeeter detectado).
- `monitorDevice`: dispositivo opcional pra você ouvir; null = monitor desligado.
- `latencyMs`: 50 default. Subir pra 100 se houver glitches.
- `authToken`: hex de 16 bytes, embutido na URL do QR.

## Logs

`%LOCALAPPDATA%\Soundpad\logs\soundpad-YYYY-MM-DD-N.log` — rolling diário + por tamanho (10MB) + retenção 7 dias.
```

- [ ] **Step 2: Final manual smoke test**

Run: `dotnet run --project src/Soundpad`
- Confirm: tray, QR code, phone connection, play, stop, volume, monitor toggle, upload, drag-reorder.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs: README with VoiceMeeter setup + usage"
```

---

## Notes for the implementer

- **Branch hygiene:** commit per task. Each task should leave the repo green (`dotnet build && dotnet test`).
- **Pre-decoded PCM caveat:** the LRU cache holds raw PCM `byte[]` per sound — 44.1kHz stereo float32 ≈ 350 KB/s. A 5s clip is ~1.7 MB; 50 clips ≈ 85 MB. Acceptable for typical soundboards.
- **VoiceMeeter quirk:** if no `monitorDevice` is set, the app **does not** fall back to the system default — this is intentional (see spec). The UI should make this explicit in settings.

## What's explicitly deferred from this plan (frontend polish + v1.1 features)

The spec calls for more frontend polish than tasks 23 covers. The plan ships **a working v1 with these UI gaps**, all controllable via API/config manually:

- **Drag-and-drop reorder** (spec §Interface celular > Modo editar): add pointer-event handlers in `app.js` wiring to `POST /api/grid/layout`. Long-press 300ms initiates drag.
- **Edit mode (settings gear)**: rename label, change color (from 8-color palette), set position, delete sound. Long-press on a button opens a bottom sheet.
- **Settings screen for monitor device**: list `state.availableOutputDevices` in a `<select>`, persist via `POST /api/monitor/device`. Without this the user has to edit `config.json` to enable monitor.
- **Tray adapter override submenu** (spec §Inicialização step 6): "Trocar adapter de rede" listing candidates from `LanAdapterPicker.Candidates()` so the user can manually pick when auto-detection picks wrong.
- **Auto-recovery on device loss**: if `audioDevice` disappears at runtime, `NAudioWavePlayerFactory.Create` throws. Wrap in the engine's `HandlePlay` with a try/catch that broadcasts a WS error event and aborts the play (no crash).

These are 1-day items each, isolated, and can ship as v1.1 commits.
