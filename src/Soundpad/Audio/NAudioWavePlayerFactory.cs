using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundpad.Audio;

public class NAudioWavePlayerFactory : IWavePlayerFactory
{
    /// <summary>
    /// <paramref name="deviceId"/> is the stable WASAPI endpoint id (e.g.
    /// <c>{0.0.0.00000000}.{guid}</c>) — not the FriendlyName. The engine
    /// stores ids in config and passes them straight through.
    /// </summary>
    public IWavePlayer Create(string deviceId, int latencyMs)
    {
        using var en = new MMDeviceEnumerator();
        var device = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => string.Equals(d.ID, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Device not found by id: {deviceId}");
        return new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMs);
    }
}
