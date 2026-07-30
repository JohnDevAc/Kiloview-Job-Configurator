using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KiloviewSetup.Core;

public static class NetworkAddressing
{
    public static int GetPrefixLength(uint mask)
    {
        var prefixLength = 0;
        var foundZero = false;
        for (var bit = 31; bit >= 0; bit--)
        {
            var set = (mask & (1u << bit)) != 0;
            if (set && foundZero) throw new ArgumentException("Subnet mask must contain contiguous one bits.");
            if (set) prefixLength++;
            else foundZero = true;
        }
        return prefixLength;
    }

    public static bool Contains(IPAddress address, IPAddress networkAddress, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork
            || networkAddress.AddressFamily != AddressFamily.InterNetwork
            || prefixLength is < 0 or > 32)
            return false;
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return (ToUInt(address) & mask) == (ToUInt(networkAddress) & mask);
    }

    public static IReadOnlyList<LocalNetworkInterface> GetLocalInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up
            && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback
            && network.NetworkInterfaceType is not NetworkInterfaceType.Tunnel)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address.Address))
            .Select(address => new LocalNetworkInterface(
                network.Name,
                network.Description,
                address.Address.ToString(),
                address.PrefixLength,
                network.NetworkInterfaceType.ToString())))
        .DistinctBy(candidate => candidate.Address)
        .OrderBy(candidate => candidate.Type == nameof(NetworkInterfaceType.Ethernet) ? 0
            : candidate.Type == nameof(NetworkInterfaceType.Wireless80211) ? 1 : 2)
        .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
        .ToArray();

    public static int DiscoveryParallelism(int addressCount)
    {
        if (addressCount <= 0) return 1;
        var machineLimit = Math.Clamp(Environment.ProcessorCount * 8, 32, 64);
        return Math.Min(addressCount, machineLimit);
    }

    public static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    public static IPAddress FromUInt(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return new IPAddress(bytes);
    }

    public static IReadOnlyList<string> GetLocalScanCidrs() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a =>
        {
            var originalPrefix = a.PrefixLength;
            var prefix = Math.Max(originalPrefix, 24); // Safe discovery default: never sweep more than 254 hosts automatically.
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            return $"{FromUInt(ToUInt(a.Address) & mask)}/{prefix}";
        })
        .Distinct()
        .Order()
        .ToArray();

    public static IEnumerable<IPAddress> ExpandCidr(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix is < 20 or > 30)
            throw new ArgumentException($"Scan range '{cidr}' must be an IPv4 CIDR between /20 and /30.");
        var ip = InputValidation.Ip(parts[0], "Scan range");
        var mask = uint.MaxValue << (32 - prefix);
        var network = ToUInt(ip) & mask;
        var broadcast = network | ~mask;
        for (var current = network + 1; current < broadcast; current++) yield return FromUInt(current);
    }

    public static IEnumerable<IPAddress> Range(string start, string end)
    {
        var first = ToUInt(InputValidation.Ip(start, "Range start"));
        var last = ToUInt(InputValidation.Ip(end, "Range end"));
        for (var current = first; current <= last; current++)
        {
            yield return FromUInt(current);
            if (current == uint.MaxValue) yield break;
        }
    }
}
