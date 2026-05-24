using Soundpad.Models;

namespace Soundpad.Api.Dto;

public record SoundEntryDto(
    string Id, string File, string Label, string Color, string? Icon, GridPosition? Position, bool Missing,
    // Schema v3 usage stats. PlayCount is the total accepted Plays for this
    // sound (server-authoritative; no client-side mutation endpoint).
    // LastPlayedAt is UTC ISO-8601 — the frontend localizes.
    int PlayCount = 0, DateTime? LastPlayedAt = null,
    // Schema v4: per-sound volume 0-100 (UI scale). Defaults to 100 for any
    // older client that doesn't model it.
    int Volume = 100,
    // Schema v5: computed normalization gain in dB (null until the analyzer
    // has run for this sound). Read-only from the client's perspective.
    double? NormalizeGainDb = null);

/// <summary>Lightweight board summary used by /api/state and /api/boards.</summary>
public record BoardSummaryDto(string Id, string Name, string Color);

/// <summary>
/// State emitted to the WPF window and phone web UI.
///
/// <para>Device identity model: <see cref="AudioDevice"/>, <see cref="MonitorDevice"/>,
/// and <see cref="MicDevice"/> carry the persisted, stable <b>endpoint ids</b>
/// (round-trippable into POST bodies). The corresponding <c>*DeviceName</c>
/// fields are the current FriendlyNames resolved at the moment of state
/// emission — for display only, and null if the device is currently unplugged.</para>
///
/// <para>Multi-board model (schema v4+): <see cref="Boards"/> lists every board
/// and <see cref="ActiveBoardId"/> identifies which one the rest of the
/// payload describes (Grid + Sounds reflect the active board only). Legacy
/// clients that don't render the boards list still get a working single-board
/// experience because Grid + Sounds are at the same top-level shape as v3.</para>
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
    string? MicDeviceName = null,
    // Schema v4: multi-board surface. Always non-null in practice (the library
    // backfills a default board on Load), but typed nullable so the record's
    // default constructor remains usable by hand-written test fixtures.
    IReadOnlyList<BoardSummaryDto>? Boards = null,
    string? ActiveBoardId = null,
    // Schema v5: global normalization toggle. Defaults to true so older clients
    // that don't render the toggle still see normalized playback by default.
    bool NormalizeEnabled = true);
