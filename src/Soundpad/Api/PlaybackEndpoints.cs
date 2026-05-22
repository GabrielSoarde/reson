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
                AvailableInputDevices: SafeListInputs(loc),
                // FriendlyName projections — resolved on each /state call so
                // unplug/replug is reflected without an explicit refresh hook.
                AudioDeviceName: loc.ResolveCurrentName(lib.Config.AudioDevice),
                MonitorDeviceName: loc.ResolveCurrentName(lib.Config.MonitorDevice),
                MicDeviceName: loc.ResolveCurrentName(lib.Config.MicDevice));
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
            // Body carries an endpoint id, but we also accept a FriendlyName
            // for back-compat with old clients/scripts that still POST names.
            // ResolveDeviceInput returns null if the input matches nothing.
            string? deviceId = ResolveDeviceInput(body.Device, loc, DataFlow.Render);
            if (body.Device is not null && deviceId is null)
                return Results.BadRequest(new { error = "unknown_device", available = loc.EnumerateRenderDeviceNames() });
            // Block monitor == game device: routing both to the same output
            // would double the gain and echo the sound. Compare by id.
            if (deviceId is not null && lib.Config.AudioDevice is not null &&
                string.Equals(deviceId, lib.Config.AudioDevice, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "monitor_equals_game_device" });
            engine.SetMonitorDevice(deviceId);
            lib.MutateConfig(c => c with { MonitorDevice = deviceId });
            lib.Save();
            await hub.BroadcastAsync("monitorDeviceChanged", new { device = deviceId }, OriginIdOf(http));
            return Results.NoContent();
        });

        // Game-output device picker. Mirror of /monitor/device: accepts either
        // endpoint id or FriendlyName, persists the canonical id, enforces the
        // monitor != game loop guard, broadcasts gameDeviceChanged with the
        // caller's X-Origin-Id so the WPF window can echo-filter its own request.
        g.MapPost("/game/device", async (GameDeviceBody body, SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc, StateHub hub, HttpContext http) =>
        {
            string? deviceId = ResolveDeviceInput(body.Device, loc, DataFlow.Render);
            if (body.Device is not null && deviceId is null)
                return Results.BadRequest(new { error = "unknown_device", available = loc.EnumerateRenderDeviceNames() });
            // Mirror of the monitor guard: a game device equal to the monitor
            // device would double-mix the sound. Different error code so the
            // UI can distinguish which direction the user picked from.
            if (deviceId is not null && lib.Config.MonitorDevice is not null &&
                string.Equals(deviceId, lib.Config.MonitorDevice, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "game_equals_monitor_device" });
            engine.SetGameDevice(deviceId);
            lib.MutateConfig(c => c with { AudioDevice = deviceId });
            lib.Save();
            await hub.BroadcastAsync("gameDeviceChanged", new { device = deviceId }, OriginIdOf(http));
            return Results.NoContent();
        });

        g.MapPost("/mic/device", async (MicDeviceBody body, SoundLibrary lib, PlaybackEngine engine, DeviceLocator loc, StateHub hub, HttpContext http) =>
        {
            // null = revert to system default capture device — always valid.
            string? deviceId = null;
            if (body.Device is not null)
            {
                deviceId = ResolveDeviceInput(body.Device, loc, DataFlow.Capture);
                if (deviceId is null)
                {
                    IReadOnlyList<string> available;
                    try { available = loc.EnumerateCaptureDeviceNames(); }
                    catch { available = Array.Empty<string>(); }
                    return Results.BadRequest(new { error = "unknown_device", available });
                }
            }
            engine.SetMicDevice(deviceId);
            lib.MutateConfig(c => c with { MicDevice = deviceId });
            lib.Save();
            await hub.BroadcastAsync("micDeviceChanged", new { device = deviceId }, OriginIdOf(http));
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Accept either an endpoint id (preferred) or a FriendlyName (legacy)
    /// and return the canonical id, or null if unknown. The flow argument
    /// restricts which side of the enumeration is checked first.
    /// </summary>
    private static string? ResolveDeviceInput(string? input, DeviceLocator loc, DataFlow flow)
    {
        if (string.IsNullOrEmpty(input)) return null;

        // Already an id?
        if (loc.DeviceExists(input)) return input;

        // Try as FriendlyName — search the matching flow first, then fall
        // back to FindIdByName (which checks both flows).
        var devices = flow == DataFlow.Render
            ? loc.EnumerateRenderDevices()
            : loc.EnumerateCaptureDevices();
        foreach (var d in devices)
            if (string.Equals(d.FriendlyName, input, StringComparison.OrdinalIgnoreCase))
                return d.Id;

        return loc.FindIdByName(input);
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

    /// <summary>Flow discriminator used by ResolveDeviceInput.</summary>
    private enum DataFlow { Render, Capture }

    public record VolumeBody(int Value);
    public record MonitorBody(bool Enabled);
    public record MonitorDeviceBody(string? Device);
    public record MicDeviceBody(string? Device);
    public record GameDeviceBody(string? Device);
}
