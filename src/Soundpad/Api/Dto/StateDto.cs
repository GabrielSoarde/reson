using Soundpad.Models;

namespace Soundpad.Api.Dto;

public record SoundEntryDto(
    string Id, string File, string Label, string Color, string? Icon, GridPosition? Position, bool Missing,
    // Schema v3 usage stats. PlayCount is the total accepted Plays for this
    // sound (server-authoritative; no client-side mutation endpoint).
    // LastPlayedAt is UTC ISO-8601 — the frontend localizes.
    int PlayCount = 0, DateTime? LastPlayedAt = null);

/// <summary>
/// State emitted to the WPF window and phone web UI.
///
/// <para>Device identity model: <see cref="AudioDevice"/>, <see cref="MonitorDevice"/>,
/// and <see cref="MicDevice"/> carry the persisted, stable <b>endpoint ids</b>
/// (round-trippable into POST bodies). The corresponding <c>*DeviceName</c>
/// fields are the current FriendlyNames resolved at the moment of state
/// emission — for display only, and null if the device is currently unplugged.</para>
/// </summary>
public record StateDto(
    string? AudioDevice, string? MonitorDevice, bool MonitorEnabled,
    IReadOnlyList<string> AvailableOutputDevices,
    int Volume, GridLayout Grid,
    IReadOnlyList<SoundEntryDto> Sounds,
    string? NowPlaying, bool AuthRequired,
    string? MicDevice = null,
    IReadOnlyList<string>? AvailableInputDevices = null,
    string? AudioDeviceName = null,
    string? MonitorDeviceName = null,
    string? MicDeviceName = null);
