using System.Net;
using System.Net.NetworkInformation;

namespace Soundpad.Network;

public class LanAdapterPicker
{
    private static readonly string[] Blocklist =
    {
        "vethernet", "virtualbox", "vmware", "wsl", "hyper-v",
        "tailscale", "hamachi", "loopback", "bluetooth"
    };

    private readonly INetworkInterfaceProvider _provider;

    public LanAdapterPicker(INetworkInterfaceProvider provider) { _provider = provider; }

    public NetworkAdapter Pick(string? preferredId)
    {
        var all = _provider.GetAll();
        if (preferredId is not null)
        {
            var pref = all.FirstOrDefault(a => a.Id == preferredId && a.IsUp && a.IPv4.Any(IsPrivate));
            if (pref is not null) return pref;
        }

        return all
            .Where(a => a.IsUp && a.IPv4.Any(IsPrivate))
            .Where(a => !IsBlocked(a))
            .OrderByDescending(a => a.Gateways.Count > 0)
            .ThenByDescending(a => a.Type is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
            .FirstOrDefault()
            ?? FallbackLoopback();
    }

    public IReadOnlyList<NetworkAdapter> Candidates() =>
        _provider.GetAll().Where(a => a.IsUp && a.IPv4.Any(IsPrivate)).ToList();

    private static bool IsBlocked(NetworkAdapter a)
    {
        var hay = (a.Name + " " + a.Description).ToLowerInvariant();
        return Blocklist.Any(b => hay.Contains(b));
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    private static NetworkAdapter FallbackLoopback() =>
        new("loopback", "Loopback", "Loopback", NetworkInterfaceType.Loopback, true,
            new[] { IPAddress.Loopback }, Array.Empty<IPAddress>());
}
