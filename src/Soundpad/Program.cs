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

var rootDir = AppContext.BaseDirectory;
Directory.CreateDirectory(Path.Combine(rootDir, "sounds"));

// Ensure %LOCALAPPDATA%\Soundpad\logs\ exists for the rolling file logger (skipped under Testing).
string? logDir = null;
if (!isTesting)
{
    logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Soundpad", "logs");
    Directory.CreateDirectory(logDir);
}
var libOpts = new SoundLibraryOptions(rootDir);

var library = new SoundLibrary(libOpts);
library.Load();
library.RepairInvariants();
library.AutoScan();

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

var app = builder.Build();

if (!isTesting && library.Config.AudioDevice is null)
{
    try
    {
        var loc = app.Services.GetRequiredService<DeviceLocator>();
        var vm = loc.FindVoiceMeeterInput();
        if (vm is not null) { library.MutateConfig(c => c with { AudioDevice = vm }); library.Save(); }
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
engine.SetVolume(library.Config.Volume);
engine.SetLatency(library.Config.LatencyMs);
engine.Start();

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

// Spin up the WPF native window + tray on its own STA thread. The WPF
// Application owns the tray icon; Kestrel keeps blocking on the main thread.
// Skipped under Testing so the WebApplicationFactory can host the API without
// pulling in a UI thread.
WpfHost? wpf = null;
if (!isTesting && adapter is not null)
{
    wpf = new WpfHost(library, engine, adapter, boundPort);
    wpf.Start();
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    try { wpf?.Stop(); } catch { /* best effort */ }
    engine.Shutdown();
});

if (!isTesting && adapter is not null)
{
    Console.WriteLine($"Soundpad rodando em http://{adapter.IPv4.First()}:{boundPort}/?t=<token-redacted>");
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

public partial class Program { }
