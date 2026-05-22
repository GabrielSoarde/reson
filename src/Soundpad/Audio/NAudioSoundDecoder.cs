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
