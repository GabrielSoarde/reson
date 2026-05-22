namespace Soundpad.Sound;

public record SoundLibraryOptions(string RootDir)
{
    public string ConfigPath => Path.Combine(RootDir, "config.json");
    public string SoundsDir => Path.Combine(RootDir, "sounds");
}
