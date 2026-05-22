using NAudio.Wave;

namespace Soundpad.Audio;

public interface IWavePlayerFactory
{
    /// <summary>
    /// Create a WasapiOut bound to the device with the given <b>endpoint id</b>
    /// (stable WASAPI identifier, NOT the FriendlyName).
    /// </summary>
    IWavePlayer Create(string deviceId, int latencyMs);
}
