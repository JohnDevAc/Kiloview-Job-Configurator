using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KiloviewSetup.Core;

public static class NetworkAddressing
{
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
