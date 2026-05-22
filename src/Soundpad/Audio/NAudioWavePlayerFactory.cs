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
