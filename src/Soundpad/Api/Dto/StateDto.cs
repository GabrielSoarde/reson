using Soundpad.Models;

namespace Soundpad.Api.Dto;

public record SoundEntryDto(string Id, string File, string Label, string Color, string? Icon, GridPosition? Position, bool Missing);

public record StateDto(
    string? AudioDevice, string? MonitorDevice, bool MonitorEnabled,
    IReadOnlyList<string> AvailableOutputDevices,
    int Volume, GridLayout Grid,
    IReadOnlyList<SoundEntryDto> Sounds,
    string? NowPlaying, bool AuthRequired,
    string? MicDevice = null,
    IReadOnlyList<string>? AvailableInputDevices = null);
