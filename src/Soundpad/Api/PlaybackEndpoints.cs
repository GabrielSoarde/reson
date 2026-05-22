using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
                engine.NowPlaying, AuthRequired: true,
                MicDevice: lib.Config.MicDevice,
                AvailableInputDevices: SafeListInputs(loc));
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

        // Volume/monitor endpoints broadcast directly so we can attach the caller's
        // X-Origin-Id (used by the browser's echo-filter). The engine still emits its
        // own events for internal listeners, but the Program.cs WS broadcast wiring
        // skips these three event types to avoid double-broadcasts.
        g.MapPost("/volume", async (VolumeBody body, SoundLibrary lib, PlaybackEngine engine, StateHub hub, HttpContext http) =>
        {
            var v = Math.Clamp(body.Value, 0, 100);
            engine.SetVolume(v);
            lib.MutateConfig(c => c with { Volume = v });
            lib.Save();
            await hub.BroadcastAsync("volumeChanged", new { value = v }, OriginIdOf(http));
            return Results.NoContent();
        });

        g.MapPost("/monitor", async (MonitorBody body, SoundLibrary lib, PlaybackEngine engine, StateHub hub, HttpContext http) =>
        {
            engine.SetMonitorEnabled(body.Enabled);
            lib.MutateConfig(c => c with { MonitorEnabled = body.Enabled });
            lib.Save();
            await hub.BroadcastAsync("monitorChanged", new { enabled = body.Enabled }, OriginIdOf(http));
            return Results.NoContent();
        });

        g.MapPost("/monitor/device", async (MonitorDeviceBody body, SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc, StateHub hub, HttpContext http) =>
        {
            if (body.Device is not null && !loc.EnumerateRenderDeviceNames().Contains(body.Device, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "unknown_device", available = loc.EnumerateRenderDeviceNames() });
            // Block monitor == game device: routing both to the same output
            // would double the gain and echo the sound (it plays through
            // the cable AND through the user's own monitor).
            if (body.Device is not null && lib.Config.AudioDevice is not null &&
                string.Equals(body.Device, lib.Config.AudioDevice, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "monitor_equals_game_device" });
            engine.SetMonitorDevice(body.Device);
            lib.MutateConfig(c => c with { MonitorDevice = body.Device });
            lib.Save();
            await hub.BroadcastAsync("monitorDeviceChanged", new { device = body.Device }, OriginIdOf(http));
            return Results.NoContent();
        });

        g.MapPost("/mic/device", async (MicDeviceBody body, SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc, StateHub hub, HttpContext http) =>
        {
            // null = revert to system default capture device — always valid.
            if (body.Device is not null)
            {
                IReadOnlyList<string> available;
                try { available = loc.EnumerateCaptureDeviceNames(); }
                catch { available = Array.Empty<string>(); }
                if (!available.Contains(body.Device, StringComparer.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "unknown_device", available });
            }
            engine.SetMicDevice(body.Device);
            lib.MutateConfig(c => c with { MicDevice = body.Device });
            lib.Save();
            await hub.BroadcastAsync("micDeviceChanged", new { device = body.Device }, OriginIdOf(http));
            return Results.NoContent();
        });
    }

    private static IReadOnlyList<string> SafeListInputs(DeviceLocator loc)
    {
        try { return loc.EnumerateCaptureDeviceNames(); }
        catch { return Array.Empty<string>(); }
    }

    private static string? OriginIdOf(HttpContext ctx)
    {
        var v = ctx.Request.Headers["X-Origin-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public record VolumeBody(int Value);
    public record MonitorBody(bool Enabled);
    public record MonitorDeviceBody(string? Device);
    public record MicDeviceBody(string? Device);
}
