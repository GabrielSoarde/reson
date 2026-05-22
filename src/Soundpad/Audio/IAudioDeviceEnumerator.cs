namespace Soundpad.Audio;

public record AudioDeviceInfo(string Id, string FriendlyName);

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices();
    IReadOnlyList<AudioDeviceInfo> EnumerateCaptureDevices();
}
