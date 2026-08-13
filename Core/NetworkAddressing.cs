using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KiloviewSetup.Core;

public static class NetworkAddressing
{
    public static LocalNetworkInterface? ResolveLocalInterface(string? adapterId, string? address)
    {
        var candidates = GetLocalInterfaces();
        if (!string.IsNullOrWhiteSpace(adapterId))
        {
            var exact = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, adapterId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(address)
                    || string.Equals(candidate.Address, address, StringComparison.Ordinal)));
            if (exact is not null) return exact;
            var adapterCandidates = candidates
                .Where(candidate => string.Equals(candidate.Id, adapterId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (adapterCandidates.Length == 1) return adapterCandidates[0];
        }
        if (!string.IsNullOrWhiteSpace(address))
            return candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Address, address, StringComparison.Ordinal));
        return null;
    }

    public static string GetScanCidr(LocalNetworkInterface network)
    {
        var address = InputValidation.Ip(network.Address, "Network adapter address");
        var prefix = Math.Clamp(network.PrefixLength, 24, 30);
        var mask = uint.MaxValue << (32 - prefix);
        return $"{FromUInt(ToUInt(address) & mask)}/{prefix}";
    }

    public static string GetNetworkCidr(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32)
            throw new ArgumentException("A valid IPv4 address and prefix length are required.");
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return $"{FromUInt(ToUInt(address) & mask)}/{prefixLength}";
    }

    public static IReadOnlyList<string> GetSenderSubnets(
        IEnumerable<string> senderAddresses,
        LocalNetworkInterface localNetwork)
    {
        var localAddress = InputValidation.Ip(localNetwork.Address, "Network adapter address");
        return senderAddresses
            .Select(address => InputValidation.Ip(address, "NDI sender address"))
            .Where(address => !address.Equals(localAddress))
            .Select(address => Contains(address, localAddress, localNetwork.PrefixLength)
                ? GetNetworkCidr(address, localNetwork.PrefixLength)
                : GetNetworkCidr(address, 32))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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
                network.Id,
                network.Name,
                network.Description,
                address.Address.ToString(),
                address.PrefixLength,
                network.NetworkInterfaceType.ToString())))
        .DistinctBy(candidate => (candidate.Id, candidate.Address))
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
