using Microsoft.AspNetCore.Http.Features;
using Soundpad;
using Soundpad.Audio;
using Soundpad.Logging;
using Soundpad.Network;
using Soundpad.Sound;
using Soundpad.Wpf;

// Detect the Testing environment (set by WebApplicationFactory in integration tests)
// BEFORE any side effects so we can skip port binding, adapter detection, file writes,
// and tray UI that would race with concurrent test fixtures.
var isTesting = string.Equals(
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
    "Testing", StringComparison.OrdinalIgnoreCase);

// User-writable data lives in %LOCALAPPDATA%\Reson\ (config.json + sounds/).
// The exe lives in Program Files which is read-only for non-admin processes —
// trying to write config.json.tmp there raises UnauthorizedAccessException.
// In Testing mode we keep rootDir = AppContext.BaseDirectory so test fixtures'
// content-root pinning continues to work.
var rootDir = isTesting
    ? AppContext.BaseDirectory
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Reson");

// One-shot migration from the old "Soundpad" data folder. If Reson/ doesn't
// exist yet but Soundpad/ does, copy everything (config.json + sounds/ + logs/)
// so existing installs keep their library + auth token. We leave the legacy
// folder in place — the user can delete it manually after confirming Reson
// works as expected. Failures are non-fatal: app falls back to a fresh root.
if (!isTesting)
{
    var legacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Soundpad");
    if (!Directory.Exists(rootDir) && Directory.Exists(legacyRoot))
    {
        try
        {
            CopyDirectoryRecursive(legacyRoot, rootDir);
            Console.WriteLine($"Migrated user data: {legacyRoot} -> {rootDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Legacy Soundpad data migration failed (non-fatal): {ex.Message}");
        }
    }
}

Directory.CreateDirectory(rootDir);
Directory.CreateDirectory(Path.Combine(rootDir, "sounds"));

// On first run, seed sounds from the install directory's sample bundle (if present).
// The installer drops sample mp3s next to the exe; we copy them once to the
// user-writable sounds folder so the default grid isn't empty.
if (!isTesting)
{
    var seedDir = Path.Combine(AppContext.BaseDirectory, "sounds");
    var userSoundsDir = Path.Combine(rootDir, "sounds");
    if (Directory.Exists(seedDir) && !Directory.EnumerateFileSystemEntries(userSoundsDir).Any())
    {
        foreach (var src in Directory.EnumerateFiles(seedDir))
        {
            try { File.Copy(src, Path.Combine(userSoundsDir, Path.GetFileName(src)), overwrite: false); }
            catch { /* best effort; user can copy manually */ }
        }
    }
}

// Ensure %LOCALAPPDATA%\Reson\logs\ exists for the rolling file logger (skipped under Testing).
string? logDir = null;
if (!isTesting)
{
    logDir = Path.Combine(rootDir, "logs");
    Directory.CreateDirectory(logDir);
}
var libOpts = new SoundLibraryOptions(rootDir);

var library = new SoundLibrary(libOpts);
library.Load();
// v1 → v2 device-identifier migration runs before any code consults
// library.Config.AudioDevice/MonitorDevice/MicDevice. The locator depends on
// IAudioDeviceEnumerator — instantiate one explicitly here (DI isn't built
// yet). Under Testing we use the same WASAPI enumerator; the migration is a
// silent no-op when device fields are already null or already-ids.
if (!isTesting)
{
    try
    {
        var bootLocator = new DeviceLocator(new WasapiAudioDeviceEnumerator());
        library.MigrateDeviceIdentifiers(bootLocator);
    }
    catch (Exception ex)
    {
        // Headless or driver-less environment: migration becomes a no-op and
        // the user re-picks devices from the UI.
        Console.Error.WriteLine($"Device identifier migration skipped: {ex.Message}");
    }
}
library.RepairInvariants();
// Note: no AutoScan() here. Folder-seeding happens once inside Load() on first
// run. Re-scanning on every boot would re-import sounds the user intentionally
// removed from a board (the file stays on disk by design).

int boundPort = isTesting ? library.Config.Port : PickPort(library.Config.Port);
if (!isTesting && boundPort != library.Config.Port)
{
    library.MutateConfig(c => c with { Port = boundPort });
    library.Save();
}

NetworkAdapter? adapter = null;
if (!isTesting)
{
    var netProvider = new SystemNetworkInterfaceProvider();
    var picker = new LanAdapterPicker(netProvider);
    adapter = picker.Pick(library.Config.PreferredNetworkAdapter);
    if (adapter.Id != library.Config.PreferredNetworkAdapter)
    {
        library.MutateConfig(c => c with { PreferredNetworkAdapter = adapter.Id });
        library.Save();
    }
}

var builder = WebApplication.CreateBuilder(args);

// Configure logging: console always; rolling file provider skipped in Testing env
// to avoid creating log files during integration tests / CI.
if (!isTesting)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddProvider(new RollingFileLoggerProvider(logDir!, 10 * 1024 * 1024, 7));
}

if (!isTesting)
{
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.MaxRequestBodySize = SoundLibrary.MaxUploadBytes;
        o.ListenAnyIP(boundPort);
    });
}
else
{
    builder.WebHost.ConfigureKestrel(o =>
    {
        o.Limits.MaxRequestBodySize = SoundLibrary.MaxUploadBytes;
    });
}
builder.Services.Configure<FormOptions>(o => { o.MultipartBodyLengthLimit = SoundLibrary.MaxUploadBytes; });

builder.Services.AddSingleton(library);
builder.Services.AddSingleton(new AppOptions(rootDir));
builder.Services.AddSingleton<IAudioDeviceEnumerator, WasapiAudioDeviceEnumerator>();
builder.Services.AddSingleton<DeviceLocator>();
builder.Services.AddSingleton<IWavePlayerFactory, NAudioWavePlayerFactory>();
builder.Services.AddSingleton<ISoundDecoder, NAudioSoundDecoder>();
builder.Services.AddSingleton(sp => new SoundCache(sp.GetRequiredService<ISoundDecoder>(), 50));
builder.Services.AddSingleton<PlaybackEngine>();
builder.Services.AddSingleton<Soundpad.Api.StateHub>();
// Hot-plug listener is WASAPI-bound — skipped under Testing so the
// WebApplicationFactory doesn't pull in real COM registrations.
if (!isTesting)
{
    builder.Services.AddSingleton<IDeviceChangeNotifier, DeviceChangeNotifier>();
}

var app = builder.Build();

if (!isTesting && library.Config.AudioDevice is null)
{
    try
    {
        var loc = app.Services.GetRequiredService<DeviceLocator>();
        var bridge = loc.FindVirtualAudioBridge();
        if (bridge is not null) { library.MutateConfig(c => c with { AudioDevice = bridge }); library.Save(); }
    }
    catch (Exception ex)
    {
        // In headless/test environments the audio device enumerator may fail. Don't crash bootstrap.
        Console.Error.WriteLine($"VoiceMeeter auto-detection skipped: {ex.Message}");
    }
}

var engine = app.Services.GetRequiredService<PlaybackEngine>();
engine.SetGameDevice(library.Config.AudioDevice);
engine.SetMonitorDevice(library.Config.MonitorDevice);
engine.SetMonitorEnabled(library.Config.MonitorEnabled);
engine.SetMicDevice(library.Config.MicDevice);
engine.SetVolume(library.Config.Volume);
engine.SetLatency(library.Config.LatencyMs);
engine.Start();

// Hot-plug bridge: wire IMMNotificationClient → engine command queue. The
// notifier's events fire on COM threads — engine.NotifyDevice{Un,}plugged
// only enqueues a command, so it's safe to call from any thread. Disposed
// via the same ApplicationStopping hook as the engine.
IDeviceChangeNotifier? deviceNotifier = null;
if (!isTesting)
{
    try
    {
        deviceNotifier = app.Services.GetRequiredService<IDeviceChangeNotifier>();
        deviceNotifier.DeviceUnplugged += id => engine.NotifyDeviceUnplugged(id);
        deviceNotifier.DevicePlugged += id => engine.NotifyDevicePlugged(id);
        deviceNotifier.Start();
    }
    catch (Exception ex)
    {
        // Notifier failures are non-fatal — the user just won't get hot-plug
        // recovery for this session.
        Console.Error.WriteLine($"Device change notifier failed to start: {ex.Message}");
    }
}

// FileSystemWatcher on the sounds/ folder — picks up files dropped after
// boot (or removed externally) and refreshes the library without a restart.
SoundsFolderWatcher? soundsWatcher = null;
if (!isTesting)
{
    try
    {
        soundsWatcher = new SoundsFolderWatcher(library, Path.Combine(rootDir, "sounds"));
        soundsWatcher.Start();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Sounds folder watcher failed to start: {ex.Message}");
    }
}

// Preload the user's most-used sounds into the LRU cache on a background
// task so the first tap of a frequently-pressed button is hitch-free.
// Ordering: most recently played first (LastPlayedAt desc, nulls last),
// then most played (PlayCount desc). Cap at 10 entries so a long tail of
// rarely-used sounds doesn't churn the SoundCache on every boot.
// Best-effort: missing files and decode errors are swallowed silently —
// the per-Play decode path will surface them.
if (!isTesting)
{
    var cache = app.Services.GetRequiredService<SoundCache>();
    var soundsDir = Path.Combine(rootDir, "sounds");
    _ = Task.Run(() =>
    {
        // Preload spans every board — a frequently-played sound on an
        // inactive board should still hit the cache the moment the user
        // switches to that board.
        var preload = library.Config.Boards
            .SelectMany(b => b.Sounds)
            .Where(x => x.Position is not null)
            .OrderByDescending(x => x.LastPlayedAt ?? DateTime.MinValue)
            .ThenByDescending(x => x.PlayCount)
            .Take(10);
        foreach (var s in preload)
        {
            var path = Path.Combine(soundsDir, s.File);
            if (!File.Exists(path)) continue;
            try { cache.Get(path); } catch { /* best effort */ }
        }
    });
}

var hub = app.Services.GetRequiredService<Soundpad.Api.StateHub>();
engine.Playing += id => _ = hub.BroadcastAsync("playing", new { soundId = id });
engine.Stopped += () => _ = hub.BroadcastAsync("stopped", new { });
// Note: volumeChanged/monitorChanged/monitorDeviceChanged are broadcast directly
// from the HTTP endpoints in PlaybackEndpoints so the request's X-Origin-Id can be
// propagated into the envelope (enables the browser echo-filter). We deliberately
// do NOT wire engine.VolumeChanged/MonitorChanged/MonitorDeviceChanged here to
// avoid double-broadcasting on every state mutation.

app.UseMiddleware<Soundpad.Security.AuthTokenMiddleware>();
app.UseWebSockets();
app.UseStaticFiles();

// Use ContentRootPath so the path resolves correctly in both production
// (== AppContext.BaseDirectory) and integration tests (where WebApplicationFactory
// pins ContentRootPath to the SUT project dir via MvcTestingAppManifest.json).
var webRootDir = app.Environment.ContentRootPath;
app.MapGet("/", () => Results.File(Path.Combine(webRootDir, "wwwroot", "index.html"), "text/html"));
app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await hub.AcceptAsync(ctx);
});
Soundpad.Api.PlaybackEndpoints.Map(app);
Soundpad.Api.SoundEndpoints.Map(app);
Soundpad.Api.AuthEndpoints.Map(app);

// Spin up the WPF native window + tray on its own STA thread. The WPF
// Application owns the tray icon; Kestrel keeps blocking on the main thread.
// Skipped under Testing so the WebApplicationFactory can host the API without
// pulling in a UI thread.
WpfHost? wpf = null;
if (!isTesting && adapter is not null)
{
    var locator = app.Services.GetRequiredService<DeviceLocator>();
    wpf = new WpfHost(library, engine, locator, adapter, boundPort, Path.Combine(rootDir, "sounds"));
    wpf.Start();
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { soundsWatcher?.Dispose(); } catch { /* best effort */ }
    try { deviceNotifier?.Dispose(); } catch { /* best effort */ }
    try { wpf?.Stop(); } catch { /* best effort */ }
    engine.Shutdown();
});

if (!isTesting && adapter is not null)
{
    Console.WriteLine($"Reson rodando em http://{adapter.IPv4.First()}:{boundPort}/?t=<token-redacted>");
}

app.Run();

static int PickPort(int desired)
{
    for (int p = desired; p < desired + 10; p++)
    {
        try
        {
            using var s = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, p);
            s.Start(); s.Stop();
            return p;
        }
        catch (System.Net.Sockets.SocketException) { }
    }
    throw new InvalidOperationException($"No free port from {desired} to {desired + 9}");
}

// Recursive file+folder copy used only by the one-shot Soundpad -> Reson
// data-folder migration on first launch. Skips entries that already exist
// at the destination so a partially-completed migration doesn't clobber
// fresh data.
static void CopyDirectoryRecursive(string src, string dst)
{
    Directory.CreateDirectory(dst);
    foreach (var dir in Directory.EnumerateDirectories(src))
    {
        var name = Path.GetFileName(dir);
        CopyDirectoryRecursive(dir, Path.Combine(dst, name));
    }
    foreach (var file in Directory.EnumerateFiles(src))
    {
        var name = Path.GetFileName(file);
        var target = Path.Combine(dst, name);
        if (!File.Exists(target))
        {
            try { File.Copy(file, target, overwrite: false); }
            catch { /* best effort */ }
        }
    }
}

public partial class Program { }
