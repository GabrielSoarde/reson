using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Soundpad.Audio;

/// <summary>
/// <see cref="IDeviceChangeNotifier"/> backed by
/// <see cref="MMDeviceEnumerator.RegisterEndpointNotificationCallback"/>.
/// Surfaces Windows audio hot-plug events as plain .NET events.
///
/// <para><b>COM threading caveat:</b> the <c>IMMNotificationClient</c> callbacks
/// fire on a COM thread owned by the system. We forward each callback into
/// the published events synchronously — subscribers (the engine) must
/// re-marshal onto their own command queue before touching engine state.
/// Do not block in the event handlers.</para>
/// </summary>
public sealed class DeviceChangeNotifier : IDeviceChangeNotifier, IMMNotificationClient
{
    private MMDeviceEnumerator? _enumerator;
    private bool _registered;
    private bool _disposed;

    public event Action<string>? DeviceUnplugged;
    public event Action<string>? DevicePlugged;

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DeviceChangeNotifier));
        if (_registered) return;
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_registered && _enumerator is not null)
                _enumerator.UnregisterEndpointNotificationCallback(this);
        }
        catch { /* best effort — Windows occasionally races us during shutdown */ }
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
        _registered = false;
    }

    // ─── IMMNotificationClient — fired on a COM thread ───────────────────

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        // Treat anything not-Active as "unplugged" from the engine's POV.
        // Active means "ready to use"; Disabled/Unplugged/NotPresent all
        // require we drop our pipeline.
        try
        {
            if (newState == DeviceState.Active) DevicePlugged?.Invoke(deviceId);
            else DeviceUnplugged?.Invoke(deviceId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"device-notifier: OnDeviceStateChanged threw: {ex.Message}");
        }
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
        try { DevicePlugged?.Invoke(pwstrDeviceId); }
        catch (Exception ex) { Console.Error.WriteLine($"device-notifier: OnDeviceAdded threw: {ex.Message}"); }
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        try { DeviceUnplugged?.Invoke(deviceId); }
        catch (Exception ex) { Console.Error.WriteLine($"device-notifier: OnDeviceRemoved threw: {ex.Message}"); }
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Deliberate no-op: the engine only re-inits on add/remove/state-change.
        // Users with MicDevice=null already pick up the new default on the
        // next mic restart; defaulting on render hot-swap is too disruptive
        // for steady-state playback. Revisit in v2.
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // No-op: too chatty (volume changes, format changes, etc.) and
        // orthogonal to our hot-plug-only concern.
    }
}
