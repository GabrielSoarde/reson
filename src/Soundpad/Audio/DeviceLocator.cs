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

    public string? FindVbCableInput() =>
        _enumerator.EnumerateRenderDevices()
            .Select(d => d.FriendlyName)
            .FirstOrDefault(n => n.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                              && n.Contains("Input", StringComparison.OrdinalIgnoreCase)
                              && n.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Find a virtual audio cable that can route Soundpad's audio to apps like
    /// Discord/Valorant as a microphone. Tries VoiceMeeter first, then VB-Cable.
    /// Returns null if neither is installed.
    /// </summary>
    public string? FindVirtualAudioBridge() =>
        FindVoiceMeeterInput() ?? FindVbCableInput();
}
