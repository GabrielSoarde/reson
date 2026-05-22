using FluentAssertions;
using Moq;
using Soundpad.Audio;

namespace Soundpad.Tests.Audio;

public class DeviceLocatorTests
{
    private static IAudioDeviceEnumerator MakeEnum(params string[] names)
    {
        var m = new Mock<IAudioDeviceEnumerator>();
        m.Setup(e => e.EnumerateRenderDevices()).Returns(
            names.Select((n, i) => new AudioDeviceInfo($"id{i}", n)).ToList());
        return m.Object;
    }

    [Fact]
    public void FindVoiceMeeter_Detects_Standard_Input()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"));
        loc.FindVoiceMeeterInput().Should().Contain("VoiceMeeter Input");
    }

    [Fact]
    public void FindVoiceMeeter_Detects_VAIO3_Variant()
    {
        var loc = new DeviceLocator(MakeEnum("VoiceMeeter VAIO3 Input"));
        loc.FindVoiceMeeterInput().Should().Contain("VAIO3");
    }

    [Fact]
    public void FindVoiceMeeter_Detects_Aux_Variant()
    {
        var loc = new DeviceLocator(MakeEnum("VoiceMeeter Aux Input"));
        loc.FindVoiceMeeterInput().Should().Contain("Aux");
    }

    [Fact]
    public void FindVoiceMeeter_Returns_Null_When_Absent()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "Headphones"));
        loc.FindVoiceMeeterInput().Should().BeNull();
    }

    [Fact]
    public void EnumerateAll_Returns_Friendly_Names()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "Headphones", "VoiceMeeter Input"));
        loc.EnumerateRenderDeviceNames().Should().BeEquivalentTo("Speakers", "Headphones", "VoiceMeeter Input");
    }
}
