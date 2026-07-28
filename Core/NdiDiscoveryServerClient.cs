using System.Collections.Concurrent;
using System.Net.Sockets;

namespace KiloviewSetup.Core;

public sealed class NdiDiscoveryServerClient
{
    public const int DefaultPort = 5959;

    public async Task<IReadOnlyList<NdiDiscoveryServerDiscovery>> DiscoverAsync(int port, CancellationToken ct)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("NDI Discovery Server port is invalid.");

        var found = new ConcurrentDictionary<string, NdiDiscoveryServerDiscovery>(StringComparer.OrdinalIgnoreCase);
        var addresses = NetworkAddressing.GetLocalScanCidrs()
            .SelectMany(NetworkAddressing.ExpandCidr)
            .Distinct()
            .ToArray();

        await Parallel.ForEachAsync(addresses, new ParallelOptions
        {
            MaxDegreeOfParallelism = NetworkAddressing.DiscoveryParallelism(addresses.Length),
            CancellationToken = ct
        }, async (address, token) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(650));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, port, timeout.Token);
                if (client.Connected)
                    found[address.ToString()] = new(address.ToString(), port);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
        });

        return found.Values
            .OrderBy(server => NetworkAddressing.ToUInt(System.Net.IPAddress.Parse(server.ServerIp)))
            .ToArray();
    }
}
