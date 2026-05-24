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
