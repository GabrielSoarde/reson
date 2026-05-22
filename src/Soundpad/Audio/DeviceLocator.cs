namespace Soundpad.Audio;

public class DeviceLocator
{
    private readonly IAudioDeviceEnumerator _enumerator;

    public DeviceLocator(IAudioDeviceEnumerator enumerator) { _enumerator = enumerator; }

    public IReadOnlyList<string> EnumerateRenderDeviceNames() =>
        _enumerator.EnumerateRenderDevices().Select(d => d.FriendlyName).ToList();

    public string? FindVoiceMeeterInput() =>
        _enumerator.EnumerateRenderDevices()
            .Select(d => d.FriendlyName)
            .FirstOrDefault(n => n.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
                              && n.Contains("Input", StringComparison.OrdinalIgnoreCase));
}
