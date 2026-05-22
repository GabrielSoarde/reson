using System.Net;
using System.Net.NetworkInformation;

namespace Soundpad.Network;

public record NetworkAdapter(
    string Id, string Name, string Description, NetworkInterfaceType Type,
    bool IsUp, IReadOnlyList<IPAddress> IPv4, IReadOnlyList<IPAddress> Gateways);

public interface INetworkInterfaceProvider
{
    IReadOnlyList<NetworkAdapter> GetAll();
}
