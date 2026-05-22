using FluentAssertions;
using Moq;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

/// <summary>
/// Covers the ID/FriendlyName resolution helpers added in the device-by-ID
/// migration. The migration relies on these being stable in both directions:
/// <list type="bullet">
///   <item>id → FriendlyName via <see cref="DeviceLocator.ResolveCurrentName"/>,
///         used by /api/state to populate <c>AudioDeviceName</c> etc.</item>
///   <item>FriendlyName → id via <see cref="DeviceLocator.FindIdByName"/>,
///         used by the v1 → v2 schema migration.</item>
/// </list>
/// </summary>
public class DeviceIdResolutionTests
{
    private static IAudioDeviceEnumerator MakeEnum(
        (string Id, string Name)[]? render = null,
        (string Id, string Name)[]? capture = null)
    {
        var m = new Mock<IAudioDeviceEnumerator>();
        m.Setup(e => e.EnumerateRenderDevices()).Returns(
            (render ?? Array.Empty<(string, string)>())
                .Select(t => new AudioDeviceInfo(t.Id, t.Name)).ToList());
        m.Setup(e => e.EnumerateCaptureDevices()).Returns(
            (capture ?? Array.Empty<(string, string)>())
                .Select(t => new AudioDeviceInfo(t.Id, t.Name)).ToList());
        return m.Object;
    }

    [Fact]
    public void ResolveCurrentName_Returns_FriendlyName_For_Present_Id()
    {
        var loc = new DeviceLocator(MakeEnum(render: new[] {
            ("{0.0.0.00000000}.{abc}", "VoiceMeeter Input"),
            ("{0.0.0.00000000}.{def}", "Speakers"),
        }));
        loc.ResolveCurrentName("{0.0.0.00000000}.{abc}").Should().Be("VoiceMeeter Input");
    }

    [Fact]
    public void ResolveCurrentName_Returns_Null_For_Absent_Id()
    {
        var loc = new DeviceLocator(MakeEnum(render: new[] { ("idA", "Speakers") }));
        loc.ResolveCurrentName("idZ-not-here").Should().BeNull();
    }

    [Fact]
    public void ResolveCurrentName_Returns_Null_For_Null_Or_Empty()
    {
        var loc = new DeviceLocator(MakeEnum(render: new[] { ("idA", "Speakers") }));
        loc.ResolveCurrentName(null).Should().BeNull();
        loc.ResolveCurrentName("").Should().BeNull();
    }

    [Fact]
    public void ResolveCurrentName_Falls_Back_To_Capture_When_Render_Misses()
    {
        var loc = new DeviceLocator(MakeEnum(
            render: new[] { ("idR", "Speakers") },
            capture: new[] { ("idC", "Headset Mic") }));
        loc.ResolveCurrentName("idC").Should().Be("Headset Mic");
    }

    [Fact]
    public void FindIdByName_Roundtrips_FriendlyName_To_Id()
    {
        var loc = new DeviceLocator(MakeEnum(render: new[] {
            ("idVM", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"),
        }));
        loc.FindIdByName("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)")
            .Should().Be("idVM");
    }

    [Fact]
    public void FindIdByName_Is_Case_Insensitive()
    {
        var loc = new DeviceLocator(MakeEnum(render: new[] { ("idVM", "VoiceMeeter Input") }));
        loc.FindIdByName("voicemeeter input").Should().Be("idVM");
    }

    [Fact]
    public void FindIdByName_Returns_Null_For_Unknown_Name()
    {
        var loc = new DeviceLocator(MakeEnum(render: new[] { ("idA", "Speakers") }));
        loc.FindIdByName("Definitely Not A Device").Should().BeNull();
    }

    [Fact]
    public void DeviceExists_True_For_Render_And_Capture_Ids()
    {
        var loc = new DeviceLocator(MakeEnum(
            render: new[] { ("idR", "Speakers") },
            capture: new[] { ("idC", "Mic") }));
        loc.DeviceExists("idR").Should().BeTrue();
        loc.DeviceExists("idC").Should().BeTrue();
        loc.DeviceExists("idZ").Should().BeFalse();
    }

    [Fact]
    public void EnumerateRenderDevices_Returns_Full_Info_Records()
    {
        var loc = new DeviceLocator(MakeEnum(render: new[] {
            ("idA", "Speakers"),
            ("idB", "Headphones"),
        }));
        var list = loc.EnumerateRenderDevices();
        list.Should().HaveCount(2);
        list[0].Id.Should().Be("idA");
        list[0].FriendlyName.Should().Be("Speakers");
    }
}
