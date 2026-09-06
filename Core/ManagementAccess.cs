using System.Net;

namespace NDIJobConfigurator.Core;

public static class ManagementAccess
{
    public static bool Allows(IPAddress? remote, IPAddress? local, LocalNetworkInterface? selected)
    {
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        if (local?.IsIPv4MappedToIPv6 == true) local = local.MapToIPv4();
        if (remote is null) return false;
        if (IPAddress.IsLoopback(remote)) return true;
        return selected is not null && IPAddress.TryParse(selected.Address, out var address)
            && address.Equals(local) && NetworkAddressing.Contains(remote, address, selected.PrefixLength);
    }
}
