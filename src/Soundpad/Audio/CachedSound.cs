using NAudio.Wave;

namespace Soundpad.Audio;

public record CachedSound(WaveFormat Format, byte[] PcmBytes);
