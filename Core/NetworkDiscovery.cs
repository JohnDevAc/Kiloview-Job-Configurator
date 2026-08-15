using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NDIJobConfigurator.Devices;

namespace NDIJobConfigurator.Core;

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

        var state = await store.ReadAsync();
        var selectedNetwork = NetworkAddressing.ResolveLocalInterface(
            state.SelectedNetworkAdapterId,
            state.SelectedNetworkAddress)
            ?? throw new ArgumentException("Select an active network adapter before scanning for devices.");
        var cidrs = new[] { NetworkAddressing.GetScanCidr(selectedNetwork) };
        var addresses = cidrs.SelectMany(NetworkAddressing.ExpandCidr).Distinct().ToArray();
        if (addresses.Length > 8192) throw new ArgumentException("Discovery is limited to 8192 addresses per scan.");
        var credentials = request.Credentials ?? new DeviceCredentials();
        var savedKiloviews = state.Devices
            .Where(device => device.IsKiloview() && !device.IsSimulation())
            .ToArray();
        var savedCredentialsByAddress = savedKiloviews
            .GroupBy(device => device.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Credentials, StringComparer.OrdinalIgnoreCase);
        var savedCredentials = savedKiloviews
            .Select(device => device.Credentials)
            .Distinct()
            .ToArray();
        var found = new ConcurrentDictionary<string, ManagedDevice>();

        await Parallel.ForEachAsync(addresses, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = NetworkAddressing.DiscoveryParallelism(addresses.Length)
        }, async (ip, token) =>
        {
            var address = ip.ToString();
            var kiloviewTask = ProbeKiloviewAsync(
                ip,
                address,
                credentials,
                savedCredentialsByAddress.GetValueOrDefault(address),
                savedCredentials,
                token);
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
        DeviceCredentials? savedAddressCredentials,
        IReadOnlyList<DeviceCredentials> savedCredentials,
        CancellationToken ct)
    {
        if (!await HasWebPortAsync(ip, 80, ct)) return null;
        var candidates = new[] { credentials }
            .Concat(savedAddressCredentials is null ? [] : [savedAddressCredentials])
            // A previously onboarded unit may have moved or been factory-reset
            // onto a different address. Try the small locally stored credential
            // set across responding Kiloviews instead of binding it to an old IP.
            .Concat(savedCredentials)
            .Distinct()
            .ToArray();
        foreach (var candidate in candidates)
        {
            var device = await factory.ProbeAsync(address, candidate, ct);
            if (device is null) continue;
            if (device.Role == DeviceRole.Encoder)
            {
                try
                {
                    var input = await factory.Create(device).ProbeEncoderInputAsync(ct);
                    if (!input.SignalPresent)
                        device = device with { Role = DeviceRole.Decoder };
                }
                catch (Exception ex) when (ex is DeviceApiException or HttpRequestException or TaskCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    // Retain the reported hardware mode when the live input
                    // probe itself is unavailable; onboarding will retry it.
                }
            }
            return device;
        }
        return null;
    }

    private async Task<ManagedDevice?> ProbeTeleToolAsync(IPAddress ip, string address, CancellationToken ct)
    {
        if (!await HasWebPortAsync(ip, TeleToolFleetService.DefaultPort, ct)) return null;
        return await factory.ProbeTeleToolAsync(address, ct);
    }

    private static async Task<bool> HasWebPortAsync(IPAddress address, int port, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, port, timeout.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(125 * attempt), ct);
            }
        }
        return false;
    }

    private async Task MergeAsync(IReadOnlyList<ManagedDevice> devices) => await store.UpdateAsync(state =>
    {
        var existing = state.Devices
            .GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(device => device.LastSeenUtc).First(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var macAliases = existing.Values.Where(candidate =>
                !string.Equals(candidate.Id, device.Id, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(device.MacAddress) &&
                !string.IsNullOrWhiteSpace(candidate.MacAddress) &&
                string.Equals(candidate.MacAddress, device.MacAddress, StringComparison.OrdinalIgnoreCase)).ToArray();
            var old = existing.GetValueOrDefault(device.Id) ?? macAliases.OrderByDescending(candidate => candidate.LastSeenUtc).FirstOrDefault();
            foreach (var alias in macAliases) existing.Remove(alias.Id);
            existing[device.Id] = old is not null
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
            DanteAudioActive = i == 1,
            DanteAudioReady = true,
            DanteAudioStatus = i == 1 ? "Active" : "Off",
            DanteAudioDeviceLabel = "Dante AVIO USB",
            DanteAudioDetails = i == 1 ? "Dante AVIO USB audio output is running." : "Dante AVIO USB audio output is ready but stopped.",
            DanteAudioKind = "avio",
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
