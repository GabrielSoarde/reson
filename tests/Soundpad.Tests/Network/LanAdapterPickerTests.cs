using System.Net;
using System.Net.NetworkInformation;
using FluentAssertions;
using Moq;
using Soundpad.Network;

namespace Soundpad.Tests.Network;

public class LanAdapterPickerTests
{
    private static NetworkAdapter Adapter(string name, string desc, NetworkInterfaceType type, string ip, string? gateway = "192.168.1.1", bool up = true)
        => new(Guid.NewGuid().ToString(), name, desc, type, up,
               new[] { IPAddress.Parse(ip) },
               gateway is null ? Array.Empty<IPAddress>() : new[] { IPAddress.Parse(gateway) });

    [Fact]
    public void Picks_Ethernet_Over_Virtual()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("VirtualBox Host-Only", "VirtualBox", NetworkInterfaceType.Ethernet, "192.168.56.1", gateway: null),
            Adapter("Ethernet", "Realtek", NetworkInterfaceType.Ethernet, "192.168.0.42"),
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(null).IPv4[0].ToString().Should().Be("192.168.0.42");
    }

    [Fact]
    public void Skips_Loopback()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("Loopback", "Loopback", NetworkInterfaceType.Loopback, "127.0.0.1", gateway: null),
            Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99"),
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(null).Name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void Skips_Tailscale_By_Name()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("Tailscale", "Tailscale", NetworkInterfaceType.Ethernet, "100.64.0.5", gateway: null),
            Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99"),
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(null).Name.Should().Be("Wi-Fi");
    }

    [Fact]
    public void Honors_Preferred_Adapter_Id_When_Still_Present()
    {
        var pref = Adapter("Ethernet", "Realtek", NetworkInterfaceType.Ethernet, "192.168.0.42");
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[]
        {
            Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99"),
            pref,
        });
        var picker = new LanAdapterPicker(p.Object);
        picker.Pick(pref.Id).Id.Should().Be(pref.Id);
    }

    [Fact]
    public void Falls_Back_To_Auto_When_Preferred_Gone()
    {
        var p = new Mock<INetworkInterfaceProvider>();
        p.Setup(x => x.GetAll()).Returns(new[] { Adapter("Wi-Fi", "Intel", NetworkInterfaceType.Wireless80211, "192.168.0.99") });
        var picker = new LanAdapterPicker(p.Object);
        var r = picker.Pick("missing-id");
        r.Name.Should().Be("Wi-Fi");
    }
}
