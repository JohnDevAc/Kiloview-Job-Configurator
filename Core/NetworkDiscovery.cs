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

        await ClearSimulationAsync();

        var cidrs = (request.ScanCidrs is { Count: > 0 } ? request.ScanCidrs : NetworkAddressing.GetLocalScanCidrs())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (cidrs.Length == 0) throw new ArgumentException("No active IPv4 network was found. Enter a scan CIDR manually.");
        var addresses = cidrs.SelectMany(NetworkAddressing.ExpandCidr).Distinct().ToArray();
        if (addresses.Length > 8192) throw new ArgumentException("Discovery is limited to 8192 addresses per scan.");
        var credentials = request.Credentials ?? new DeviceCredentials();
        var found = new ConcurrentDictionary<string, ManagedDevice>();

        await Parallel.ForEachAsync(addresses, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 96 }, async (ip, token) =>
        {
            var address = ip.ToString();
            var kiloviewTask = ProbeKiloviewAsync(ip, address, credentials, token);
            var teleToolTask = ProbeTeleToolAsync(ip, address, token);
            await Task.WhenAll(kiloviewTask, teleToolTask);
            var kiloview = await kiloviewTask;
            var teleTool = await teleToolTask;
            if (kiloview is not null) found[kiloview.Id] = kiloview;
            if (teleTool is not null) found[teleTool.Id] = teleTool;
        });

        var devices = found.Values.OrderBy(d => NetworkAddressing.ToUInt(IPAddress.Parse(d.IpAddress))).ToArray();
        await MergeAsync(devices);
        return new(devices, cidrs, watch.Elapsed);
    }

    private async Task<ManagedDevice?> ProbeKiloviewAsync(
        IPAddress ip,
        string address,
        DeviceCredentials credentials,
        CancellationToken ct)
    {
        if (!await HasWebPortAsync(ip, 80, ct)) return null;
        return await factory.ProbeAsync(address, credentials, ct);
    }

    private async Task<ManagedDevice?> ProbeTeleToolAsync(IPAddress ip, string address, CancellationToken ct)
    {
        if (!await HasWebPortAsync(ip, TeleToolFleetService.DefaultPort, ct)) return null;
        return await factory.ProbeTeleToolAsync(address, ct);
    }

    private static async Task<bool> HasWebPortAsync(IPAddress address, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(450));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeout.Token);
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
                    HdmiOutputResolution = old.HdmiOutputResolution,
                    StreamRunning = device.StreamRunning ?? old.StreamRunning,
                    StreamStatus = device.StreamStatus ?? old.StreamStatus,
                    ActiveChannelName = device.ActiveChannelName ?? old.ActiveChannelName,
                    ActiveChannelNumber = device.ActiveChannelNumber ?? old.ActiveChannelNumber,
                    PipelineStatus = device.PipelineStatus ?? old.PipelineStatus,
                    RfSignal = device.RfSignal ?? old.RfSignal,
                    RfSignalKind = device.RfSignalKind ?? old.RfSignalKind
                }
                : device;
        }
        return state with { Devices = existing.Values.OrderBy(d => d.IpAddress).ToArray() };
    });

    private async Task<DiscoveryResult> SimulateAsync(Stopwatch watch)
    {
        var kiloviews = Enumerable.Range(1, 6).Select(i => new ManagedDevice
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
        var teleTools = Enumerable.Range(1, 2).Select(i => new ManagedDevice
        {
            Id = $"SIM-TT-{i:000}",
            IpAddress = $"192.168.10.{120 + i}",
            MacAddress = $"B8:27:EB:42:19:{i:00}",
            Hostname = $"teletool-sim{i:000}",
            Model = "TeleTool",
            Family = DeviceFamily.SimulatedTeleTool,
            Health = DeviceHealth.Online,
            Role = DeviceRole.Encoder,
            WebPort = TeleToolFleetService.DefaultPort,
            CanOnboard = true,
            ManagementState = "available",
            ManagementMessage = "Available for onboarding",
            StreamRunning = i == 1,
            StreamStatus = i == 1 ? "running" : "stopped",
            PipelineStatus = i == 1 ? "healthy" : "stopped",
            ActiveChannelName = i == 1 ? "BBC One" : null,
            ActiveChannelNumber = i == 1 ? "1" : null,
            RfSignal = i == 1 ? "-58 dBm" : "N/A",
            RfSignalKind = i == 1 ? "good" : "bad",
            TeleToolControlReady = true,
            FirmwareVersion = TeleToolFleetService.RequiredDevVersion,
            TeleToolReleaseBranch = "dev",
            LicenseAccepted = true
        }).ToArray();
        var devices = kiloviews.Concat(teleTools).ToArray();
        // Replace the previous synthetic fleet rather than merging its onboarded
        // names, groups and addresses into this new simulation run.
        await store.UpdateAsync(state => state with
        {
            Devices = state.Devices.Where(d => !d.IsSimulation())
                .Concat(devices)
                .OrderBy(d => d.IpAddress)
                .ToArray(),
            LastJob = state.LastJob?.Simulation == true ? null : state.LastJob,
            FirmwareJob = state.LastJob?.Simulation == true ? null : state.FirmwareJob
        });
        return new(devices, ["simulation"], watch.Elapsed);
    }

    private async Task ClearSimulationAsync()
    {
        titleCards.StopAll();
        await store.UpdateAsync(state => state with
        {
            Devices = state.Devices.Where(device => !device.IsSimulation()).ToArray(),
            LastJob = state.LastJob?.Simulation == true ? null : state.LastJob,
            FirmwareJob = state.LastJob?.Simulation == true ? null : state.FirmwareJob
        });
    }
}
