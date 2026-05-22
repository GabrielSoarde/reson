using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Soundpad.Api.Dto;
using Soundpad.Sound;

namespace Soundpad.Tests.Api;

public class PlaybackEndpointsTests : IClassFixture<TestingWebApplicationFactory>
{
    private readonly TestingWebApplicationFactory _factory;
    public PlaybackEndpointsTests(TestingWebApplicationFactory f) { _factory = f; }

    private HttpClient AuthedClient()
    {
        var c = _factory.CreateClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        c.DefaultRequestHeaders.Add("X-Auth-Token", lib.Config.AuthToken);
        return c;
    }

    [Fact]
    public async Task GetState_Returns_Schema()
    {
        var c = AuthedClient();
        var s = await c.GetFromJsonAsync<StateDto>("/api/state");
        s.Should().NotBeNull();
        s!.AuthRequired.Should().BeTrue();
        s.Grid.Cols.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Play_Unknown_Returns_404()
    {
        var c = AuthedClient();
        var r = await c.PostAsync("/api/play/this-does-not-exist", null);
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Volume_Persists_To_Config()
    {
        var c = AuthedClient();
        var r = await c.PostAsJsonAsync("/api/volume", new { value = 33 });
        r.IsSuccessStatusCode.Should().BeTrue();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        lib.Config.Volume.Should().Be(33);
    }

    [Fact]
    public async Task MonitorDevice_Unknown_Returns_400()
    {
        var c = AuthedClient();
        var r = await c.PostAsJsonAsync("/api/monitor/device", new { device = "Definitely Not Real" });
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MonitorDevice_Equals_GameDevice_Returns_400()
    {
        var c = AuthedClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        var loc = _factory.Services.GetRequiredService<Soundpad.Audio.DeviceLocator>();
        var devices = loc.EnumerateRenderDevices();
        if (devices.Count == 0) return; // headless CI: no playback devices to test against
        // Pin the game device to the first real device on the system. Under v2
        // config stores the stable endpoint ID — the endpoint resolves the
        // POST body (id or FriendlyName) to an ID before comparison.
        var dev = devices[0];
        lib.MutateConfig(cfg => cfg with { AudioDevice = dev.Id });
        // Same device for monitor → 400 monitor_equals_game_device. Body can
        // be the FriendlyName (legacy clients) and still match by ID.
        var r = await c.PostAsJsonAsync("/api/monitor/device", new { device = dev.FriendlyName });
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().Contain("monitor_equals_game_device");
    }

    [Fact]
    public async Task MicDevice_Null_Reverts_To_System_Default()
    {
        var c = AuthedClient();
        var r = await c.PostAsJsonAsync("/api/mic/device", new { device = (string?)null });
        r.IsSuccessStatusCode.Should().BeTrue();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        lib.Config.MicDevice.Should().BeNull();
    }

    [Fact]
    public async Task MicDevice_Unknown_Returns_400()
    {
        var c = AuthedClient();
        var r = await c.PostAsJsonAsync("/api/mic/device", new { device = "Definitely Not A Real Mic" });
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task State_Includes_MicDevice_And_AvailableInputs()
    {
        var c = AuthedClient();
        var s = await c.GetFromJsonAsync<StateDto>("/api/state");
        s.Should().NotBeNull();
        // MicDevice starts null (system default); AvailableInputDevices may
        // be empty on headless CI but must be non-null.
        s!.AvailableInputDevices.Should().NotBeNull();
    }

    [Fact]
    public async Task GameDevice_Unknown_Returns_400()
    {
        var c = AuthedClient();
        var r = await c.PostAsJsonAsync("/api/game/device", new { device = "Definitely Not A Real Output" });
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().Contain("unknown_device");
    }

    [Fact]
    public async Task GameDevice_Null_Clears_Persisted_Device()
    {
        var c = AuthedClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        // Seed a non-null value so we can prove null clears it. We don't need
        // a real device id — the engine ignores SetGameDevice(null) cleanly
        // and the persistence path is what we're asserting.
        lib.MutateConfig(cfg => cfg with { AudioDevice = "{0.0.0.x}.{seed}" });
        var r = await c.PostAsJsonAsync("/api/game/device", new { device = (string?)null });
        r.IsSuccessStatusCode.Should().BeTrue();
        lib.Config.AudioDevice.Should().BeNull();
    }

    [Fact]
    public async Task GameDevice_By_FriendlyName_Persists_Id()
    {
        var c = AuthedClient();
        var loc = _factory.Services.GetRequiredService<Soundpad.Audio.DeviceLocator>();
        var devices = loc.EnumerateRenderDevices();
        if (devices.Count == 0) return; // headless CI: no playback devices to test against
        var dev = devices[0];
        // Ensure no loop conflict — null out monitor first.
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        lib.MutateConfig(cfg => cfg with { MonitorDevice = null });
        // Post by FriendlyName; endpoint resolves to id and persists the id.
        var r = await c.PostAsJsonAsync("/api/game/device", new { device = dev.FriendlyName });
        r.IsSuccessStatusCode.Should().BeTrue();
        lib.Config.AudioDevice.Should().Be(dev.Id);
    }

    [Fact]
    public async Task GameDevice_Equals_MonitorDevice_Returns_400()
    {
        var c = AuthedClient();
        var lib = _factory.Services.GetRequiredService<SoundLibrary>();
        var loc = _factory.Services.GetRequiredService<Soundpad.Audio.DeviceLocator>();
        var devices = loc.EnumerateRenderDevices();
        if (devices.Count == 0) return; // headless CI
        var dev = devices[0];
        // Pin the monitor device to the first real device, then try to set
        // the game to the same device — must be rejected as a loop.
        lib.MutateConfig(cfg => cfg with { MonitorDevice = dev.Id });
        var r = await c.PostAsJsonAsync("/api/game/device", new { device = dev.FriendlyName });
        r.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().Contain("game_equals_monitor_device");
    }
}
