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
        m.Setup(e => e.EnumerateCaptureDevices()).Returns(Array.Empty<AudioDeviceInfo>());
        return m.Object;
    }

    private static IAudioDeviceEnumerator MakeEnumWithCapture(string[] render, string[] capture)
    {
        var m = new Mock<IAudioDeviceEnumerator>();
        m.Setup(e => e.EnumerateRenderDevices()).Returns(
            render.Select((n, i) => new AudioDeviceInfo($"r{i}", n)).ToList());
        m.Setup(e => e.EnumerateCaptureDevices()).Returns(
            capture.Select((n, i) => new AudioDeviceInfo($"c{i}", n)).ToList());
        return m.Object;
    }

    // FindVoiceMeeter* / FindVbCable* now return the device id, not the
    // FriendlyName — that's what config.json stores under the v2 schema.
    [Fact]
    public void FindVoiceMeeter_Returns_Id_Of_Standard_Input()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"));
        loc.FindVoiceMeeterInput().Should().Be("id1");
    }

    [Fact]
    public void FindVoiceMeeter_Returns_Id_Of_VAIO3_Variant()
    {
        var loc = new DeviceLocator(MakeEnum("VoiceMeeter VAIO3 Input"));
        loc.FindVoiceMeeterInput().Should().Be("id0");
    }

    [Fact]
    public void FindVoiceMeeter_Returns_Id_Of_Aux_Variant()
    {
        var loc = new DeviceLocator(MakeEnum("VoiceMeeter Aux Input"));
        loc.FindVoiceMeeterInput().Should().Be("id0");
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

    [Fact]
    public void FindVbCable_Returns_Id_Of_Standard_Input()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "CABLE Input (VB-Audio Virtual Cable)"));
        loc.FindVbCableInput().Should().Be("id1");
    }

    [Fact]
    public void FindVbCable_Returns_Null_When_Absent()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "Headphones"));
        loc.FindVbCableInput().Should().BeNull();
    }

    [Fact]
    public void FindVirtualBridge_Prefers_VoiceMeeter_Id_Over_VbCable_Id()
    {
        var loc = new DeviceLocator(MakeEnum(
            "Speakers",
            "CABLE Input (VB-Audio Virtual Cable)",
            "VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)"));
        // Voicemeeter is at index 2 → id2
        loc.FindVirtualAudioBridge().Should().Be("id2");
    }

    [Fact]
    public void FindVirtualBridge_Falls_Back_To_VbCable_Id()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "CABLE Input (VB-Audio Virtual Cable)"));
        loc.FindVirtualAudioBridge().Should().Be("id1");
    }

    [Fact]
    public void FindVirtualBridge_Returns_Null_When_Neither_Installed()
    {
        var loc = new DeviceLocator(MakeEnum("Speakers", "Headphones"));
        loc.FindVirtualAudioBridge().Should().BeNull();
    }

    [Fact]
    public void EnumerateCapture_Returns_Friendly_Names()
    {
        var loc = new DeviceLocator(MakeEnumWithCapture(
            render: new[] { "Speakers" },
            capture: new[] { "Logitech G935", "Webcam Mic" }));
        loc.EnumerateCaptureDeviceNames().Should().BeEquivalentTo("Logitech G935", "Webcam Mic");
    }
}
