using System.Net.NetworkInformation;

namespace Soundpad.Network;

public class SystemNetworkInterfaceProvider : INetworkInterfaceProvider
{
    public IReadOnlyList<NetworkAdapter> GetAll()
    {
        return NetworkInterface.GetAllNetworkInterfaces().Select(ni =>
        {
            var props = ni.GetIPProperties();
            var v4 = props.UnicastAddresses
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address).ToList();
            var gw = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(g => g.Address).ToList();
            return new NetworkAdapter(ni.Id, ni.Name, ni.Description, ni.NetworkInterfaceType,
                ni.OperationalStatus == OperationalStatus.Up, v4, gw);
        }).ToList();
    }
}
