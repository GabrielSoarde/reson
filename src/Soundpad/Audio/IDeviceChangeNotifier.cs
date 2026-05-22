namespace Soundpad.Audio;

/// <summary>
/// Abstraction over Windows audio device hot-plug notifications. Lets the
/// engine react when its game/monitor/mic device is unplugged or re-plugged
/// without polling. Tests substitute an in-memory fake (no COM apartment
/// required) via <see cref="Raise"/> helpers.
/// </summary>
/// <remarks>
/// The production implementation is backed by <c>IMMNotificationClient</c>,
/// whose callbacks fire on COM threads. Implementations must marshal events
/// onto the published <see cref="DeviceUnplugged"/> / <see cref="DevicePlugged"/>
/// delegates synchronously — subscribers (the engine) are responsible for
/// queuing work onto their own thread before touching state.
/// </remarks>
public interface IDeviceChangeNotifier : IDisposable
{
    /// <summary>
    /// Fired with the device id when a previously-active device transitions
    /// to NotPresent/Unplugged or is removed entirely. Always exactly the
    /// WASAPI endpoint id — same identity used by config.json and the engine.
    /// </summary>
    event Action<string>? DeviceUnplugged;

    /// <summary>
    /// Fired with the device id when a new endpoint appears (or a previously
    /// disabled one becomes active).
    /// </summary>
    event Action<string>? DevicePlugged;

    /// <summary>Begin listening for device changes.</summary>
    void Start();
}
