namespace Soundpad.Audio;

public abstract record AudioCommand;
public sealed record PlayCommand(string SoundId, string FilePath) : AudioCommand;
public sealed record StopCommand : AudioCommand;
public sealed record VolumeCommand(int Value) : AudioCommand;
public sealed record SetMonitorEnabledCommand(bool Enabled) : AudioCommand;
public sealed record SetMonitorDeviceCommand(string? Device) : AudioCommand;
public sealed record SetGameDeviceCommand(string? Device) : AudioCommand;
public sealed record SetMicDeviceCommand(string? Device) : AudioCommand;
public sealed record PlaybackEndedCommand(long PlayToken, bool IsGameStream) : AudioCommand;
public sealed record InitPipelineCommand : AudioCommand;
public sealed record ShutdownCommand : AudioCommand;

// Marshalled into the engine from IDeviceChangeNotifier so the engine can
// react to hot-plug events on its own STA thread (not the COM thread).
public sealed record DeviceUnpluggedCommand(string DeviceId) : AudioCommand;
public sealed record DevicePluggedCommand(string DeviceId) : AudioCommand;
