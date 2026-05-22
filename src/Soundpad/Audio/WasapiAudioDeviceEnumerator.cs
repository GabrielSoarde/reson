using NAudio.CoreAudioApi;

namespace Soundpad.Audio;

public class WasapiAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices() =>
        Enumerate(DataFlow.Render);

    public IReadOnlyList<AudioDeviceInfo> EnumerateCaptureDevices() =>
        Enumerate(DataFlow.Capture);

    private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        var list = new List<AudioDeviceInfo>();
        foreach (var dev in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            list.Add(new AudioDeviceInfo(dev.ID, dev.FriendlyName));
        return list;
    }
}
