using Soundpad.Api.Dto;
using Soundpad.Audio;
using Soundpad.Sound;

namespace Soundpad.Api;

public static class PlaybackEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/state", (SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc) =>
        {
            var statuses = lib.GetRuntimeStatuses().ToDictionary(s => s.Id, s => s.Missing);
            var entries = lib.Config.Sounds.Select(s => new SoundEntryDto(
                s.Id, s.File, s.Label, s.Color, s.Icon, s.Position,
                statuses.TryGetValue(s.Id, out var m) && m)).ToList();
            return new StateDto(
                lib.Config.AudioDevice, lib.Config.MonitorDevice, lib.Config.MonitorEnabled,
                loc.EnumerateRenderDeviceNames(),
                lib.Config.Volume, lib.Config.Grid, entries,
                engine.NowPlaying, AuthRequired: true);
        });

        g.MapPost("/play/{soundId}", (string soundId, SoundLibrary lib, PlaybackEngine engine, AppOptions opts) =>
        {
            var entry = lib.Config.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (entry is null) return Results.NotFound();
            var path = Path.Combine(opts.RootDir, "sounds", entry.File);
            if (!File.Exists(path)) return Results.NotFound();
            engine.Play(soundId, path);
            return Results.NoContent();
        });

        g.MapPost("/stop", (PlaybackEngine engine) => { engine.Stop(); return Results.NoContent(); });

        g.MapPost("/volume", (VolumeBody body, SoundLibrary lib, PlaybackEngine engine) =>
        {
            var v = Math.Clamp(body.Value, 0, 100);
            engine.SetVolume(v);
            lib.MutateConfig(c => c with { Volume = v });
            lib.Save();
            return Results.NoContent();
        });

        g.MapPost("/monitor", (MonitorBody body, SoundLibrary lib, PlaybackEngine engine) =>
        {
            engine.SetMonitorEnabled(body.Enabled);
            lib.MutateConfig(c => c with { MonitorEnabled = body.Enabled });
            lib.Save();
            return Results.NoContent();
        });

        g.MapPost("/monitor/device", (MonitorDeviceBody body, SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc) =>
        {
            if (body.Device is not null && !loc.EnumerateRenderDeviceNames().Contains(body.Device, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "unknown_device", available = loc.EnumerateRenderDeviceNames() });
            engine.SetMonitorDevice(body.Device);
            lib.MutateConfig(c => c with { MonitorDevice = body.Device });
            lib.Save();
            return Results.NoContent();
        });
    }

    public record VolumeBody(int Value);
    public record MonitorBody(bool Enabled);
    public record MonitorDeviceBody(string? Device);
}
