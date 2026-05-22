using Microsoft.AspNetCore.Http.Features;
using Soundpad;
using Soundpad.Audio;
using Soundpad.Sound;

var rootDir = AppContext.BaseDirectory;
var libOpts = new SoundLibraryOptions(rootDir);
Directory.CreateDirectory(libOpts.SoundsDir);

var library = new SoundLibrary(libOpts);
library.Load();
library.RepairInvariants();
library.AutoScan();

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = SoundLibrary.MaxUploadBytes;
    o.ListenAnyIP(library.Config.Port);
});
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = SoundLibrary.MaxUploadBytes;
});

builder.Services.AddSingleton(library);
builder.Services.AddSingleton(new AppOptions(rootDir));
builder.Services.AddSingleton<IAudioDeviceEnumerator, WasapiAudioDeviceEnumerator>();
builder.Services.AddSingleton<DeviceLocator>();
builder.Services.AddSingleton<IWavePlayerFactory, NAudioWavePlayerFactory>();
builder.Services.AddSingleton<ISoundDecoder, NAudioSoundDecoder>();
builder.Services.AddSingleton<SoundCache>(sp => new SoundCache(sp.GetRequiredService<ISoundDecoder>(), 50));
builder.Services.AddSingleton<PlaybackEngine>();
builder.Services.AddSingleton<Soundpad.Api.StateHub>();

var app = builder.Build();

app.UseMiddleware<Soundpad.Security.AuthTokenMiddleware>();

var engine = app.Services.GetRequiredService<PlaybackEngine>();
engine.SetGameDevice(library.Config.AudioDevice);
engine.SetMonitorDevice(library.Config.MonitorDevice);
engine.SetMonitorEnabled(library.Config.MonitorEnabled);
engine.SetVolume(library.Config.Volume);
engine.SetLatency(library.Config.LatencyMs);
engine.Start();

app.UseWebSockets();

var hub = app.Services.GetRequiredService<Soundpad.Api.StateHub>();
engine.Playing += id => _ = hub.BroadcastAsync("playing", new { soundId = id });
engine.Stopped += () => _ = hub.BroadcastAsync("stopped", new { });
engine.MonitorChanged += b => _ = hub.BroadcastAsync("monitorChanged", new { enabled = b });
engine.VolumeChanged += v => _ = hub.BroadcastAsync("volumeChanged", new { value = v });
engine.MonitorDeviceChanged += d => _ = hub.BroadcastAsync("monitorDeviceChanged", new { device = d });

app.Map("/ws", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    await hub.AcceptAsync(ctx);
});

app.UseStaticFiles();
app.MapGet("/", () => Results.File(Path.Combine(rootDir, "wwwroot", "index.html"), "text/html"));

app.Lifetime.ApplicationStopping.Register(() => engine.Shutdown());

app.Run();

public partial class Program { }
