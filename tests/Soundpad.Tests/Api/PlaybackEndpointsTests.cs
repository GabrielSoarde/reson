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
        var devices = loc.EnumerateRenderDeviceNames();
        if (devices.Count == 0) return; // headless CI: no playback devices to test against
        // Pin the game device to the first real device on the system.
        var dev = devices[0];
        lib.MutateConfig(cfg => cfg with { AudioDevice = dev });
        // Same device for monitor → 400 monitor_equals_game_device.
        var r = await c.PostAsJsonAsync("/api/monitor/device", new { device = dev });
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
}
