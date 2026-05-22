namespace Soundpad.Audio;

public interface ISoundDecoder
{
    CachedSound Decode(string filePath);
}
