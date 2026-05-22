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
