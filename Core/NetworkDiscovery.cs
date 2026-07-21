using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using KiloviewSetup.Devices;

namespace KiloviewSetup.Core;

public sealed class NetworkDiscovery(DeviceClientFactory factory, AppStateStore store, NdiTitleCardService titleCards)
{
    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryRequest request, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (request.Simulation)
        {
            // A simulation scan is the start of a new synthetic fleet. Stop cards
            // from any previous run so Studio Monitor cannot show stale identities.
            titleCards.StopAll();
            return await SimulateAsync(watch);
        }

        var cidrs = (request.ScanCidrs is { Count: > 0 } ? request.ScanCidrs : NetworkAddressing.GetLocalScanCidrs())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (cidrs.Length == 0) throw new ArgumentException("No active IPv4 network was found. Enter a scan CIDR manually.");
        var addresses = cidrs.SelectMany(NetworkAddressing.ExpandCidr).Distinct().ToArray();
        if (addresses.Length > 8192) throw new ArgumentException("Discovery is limited to 8192 addresses per scan.");
        var credentials = request.Credentials ?? new DeviceCredentials();
        var found = new ConcurrentDictionary<string, ManagedDevice>();

        await Parallel.ForEachAsync(addresses, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 96 }, async (ip, token) =>
        {
            if (!await HasWebPortAsync(ip, token)) return;
            var device = await factory.ProbeAsync(ip.ToString(), credentials, token);
            if (device is not null) found[device.Id] = device;
        });

        var devices = found.Values.OrderBy(d => NetworkAddressing.ToUInt(IPAddress.Parse(d.IpAddress))).ToArray();
        await MergeAsync(devices);
        return new(devices, cidrs, watch.Elapsed);
    }

    private static async Task<bool> HasWebPortAsync(IPAddress address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(450));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, 80, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }

    private async Task MergeAsync(IReadOnlyList<ManagedDevice> devices) => await store.UpdateAsync(state =>
    {
        var existing = state.Devices.ToDictionary(d => d.Id);
        foreach (var device in devices)
        {
            existing[device.Id] = existing.TryGetValue(device.Id, out var old)
                ? device with
                {
                    IsOnboarded = old.IsOnboarded,
                    NdiGroup = old.NdiGroup,
                    NdiChannelName = old.NdiChannelName,
                    HdmiDisplayConnected = old.HdmiDisplayConnected,
                    HdmiOutputResolution = old.HdmiOutputResolution
                }
                : device;
        }
        return state with { Devices = existing.Values.OrderBy(d => d.IpAddress).ToArray() };
    });

    private async Task<DiscoveryResult> SimulateAsync(Stopwatch watch)
    {
        var devices = Enumerable.Range(1, 6).Select(i => new ManagedDevice
        {
            Id = $"SIM-N{(i % 2 == 0 ? "60" : "6")}-{i:000}",
            IpAddress = $"192.168.10.{100 + i}",
            MacAddress = $"68:3A:7F:8C:A7:{i:00}",
            Hostname = $"N{(i % 2 == 0 ? "60" : "6")}-SIM{i:000}",
            Model = i % 2 == 0 ? "N60" : "N6",
            Family = DeviceFamily.Simulated,
            Health = DeviceHealth.Online,
            Role = DeviceRole.Encoder,
            HdmiDisplayConnected = i is 2 or 5,
            HdmiOutputResolution = i is 2 or 5 ? "1920x1080p60" : null,
            Credentials = new()
        }).ToArray();
        // Replace the previous synthetic fleet rather than merging its onboarded
        // names, groups and addresses into this new simulation run.
        await store.UpdateAsync(state => state with
        {
            Devices = state.Devices.Where(d => d.Family != DeviceFamily.Simulated)
                .Concat(devices)
                .OrderBy(d => d.IpAddress)
                .ToArray(),
            LastJob = state.LastJob?.Simulation == true ? null : state.LastJob,
            FirmwareJob = state.LastJob?.Simulation == true ? null : state.FirmwareJob
        });
        return new(devices, ["simulation"], watch.Elapsed);
    }
}
