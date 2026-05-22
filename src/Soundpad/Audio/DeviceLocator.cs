namespace Soundpad.Audio;

/// <summary>
/// Discovery helpers over <see cref="IAudioDeviceEnumerator"/>.
///
/// <b>Identity model:</b> persistent identifiers (config.json, engine state,
/// API request bodies) are <see cref="AudioDeviceInfo.Id"/> strings — the
/// stable WASAPI endpoint id like <c>{0.0.0.00000000}.{guid}</c>. These survive
/// Windows language changes, USB hub re-plugs, and device renames in the OS UI.
/// <see cref="AudioDeviceInfo.FriendlyName"/> is for display only and may
/// change between sessions for the same physical device.
/// </summary>
public class DeviceLocator
{
    private readonly IAudioDeviceEnumerator _enumerator;

    public DeviceLocator(IAudioDeviceEnumerator enumerator) { _enumerator = enumerator; }

    // ─── enumeration (full info; UI uses FriendlyName, plumbing uses Id) ─

    public IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices() =>
        _enumerator.EnumerateRenderDevices();

    public IReadOnlyList<AudioDeviceInfo> EnumerateCaptureDevices() =>
        _enumerator.EnumerateCaptureDevices();

    public IReadOnlyList<string> EnumerateRenderDeviceNames() =>
        _enumerator.EnumerateRenderDevices().Select(d => d.FriendlyName).ToList();

    public IReadOnlyList<string> EnumerateCaptureDeviceNames() =>
        _enumerator.EnumerateCaptureDevices().Select(d => d.FriendlyName).ToList();

    // ─── ID ↔ FriendlyName resolution ────────────────────────────────────

    /// <summary>
    /// Given a persisted device id, return the device's current FriendlyName
    /// (for UI display). Returns null if the device is no longer enumerated
    /// (unplugged / driver removed). Checks render and capture endpoints.
    /// </summary>
    public string? ResolveCurrentName(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        foreach (var d in _enumerator.EnumerateRenderDevices())
            if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) return d.FriendlyName;
        foreach (var d in _enumerator.EnumerateCaptureDevices())
            if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) return d.FriendlyName;
        return null;
    }

    /// <summary>
    /// Reverse lookup: given a FriendlyName (e.g. from a legacy v1 config or a
    /// legacy API request body), return the matching device id. Checks render
    /// endpoints first then capture. Case-insensitive. Returns null if no match.
    /// </summary>
    public string? FindIdByName(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName)) return null;
        foreach (var d in _enumerator.EnumerateRenderDevices())
            if (string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase)) return d.Id;
        foreach (var d in _enumerator.EnumerateCaptureDevices())
            if (string.Equals(d.FriendlyName, friendlyName, StringComparison.OrdinalIgnoreCase)) return d.Id;
        return null;
    }

    /// <summary>True if the given id is currently enumerated (render or capture).</summary>
    public bool DeviceExists(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        foreach (var d in _enumerator.EnumerateRenderDevices())
            if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var d in _enumerator.EnumerateCaptureDevices())
            if (string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ─── virtual-cable autodetect ────────────────────────────────────────
    // These return the device *Id*, not the FriendlyName — callers persist
    // the id into config.json so the choice survives device renames.

    public string? FindVoiceMeeterInput() =>
        _enumerator.EnumerateRenderDevices()
            .FirstOrDefault(d => d.FriendlyName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
                              && d.FriendlyName.Contains("Input", StringComparison.OrdinalIgnoreCase))?.Id;

    public string? FindVbCableInput() =>
        _enumerator.EnumerateRenderDevices()
            .FirstOrDefault(d => d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                              && d.FriendlyName.Contains("Input", StringComparison.OrdinalIgnoreCase)
                              && d.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// Find a virtual audio cable that can route Soundpad's audio to apps like
    /// Discord/Valorant as a microphone. Tries VoiceMeeter first, then VB-Cable.
    /// Returns the device <b>Id</b> (not the FriendlyName) so it's safe to persist.
    /// Returns null if neither is installed.
    /// </summary>
    public string? FindVirtualAudioBridge() =>
        FindVoiceMeeterInput() ?? FindVbCableInput();
}
