using NAudio.Wave;

namespace Soundpad.Audio;

public interface IWavePlayerFactory
{
    IWavePlayer Create(string deviceFriendlyName, int latencyMs);
}
