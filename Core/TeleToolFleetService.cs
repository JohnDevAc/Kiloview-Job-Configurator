using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NDIJobConfigurator.Devices;

namespace NDIJobConfigurator.Core;

public sealed class TeleToolFleetService(
    IHttpClientFactory clients,
    AppStateStore store,
    TeleToolFleetIdentity fleetIdentity,
    ILogger<TeleToolFleetService> logger)
{
    public const int DefaultPort = 8000;
    public const string RequiredDevVersion = "1.8.5+dev.54";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _adoptionGates = new();

    public async Task<ManagedDevice?> ProbeAsync(string ipAddress, int port, CancellationToken ct)
    {
        try
        {
            var identity = await GetAsync(ipAddress, port, "/api/manager/discovery", TimeSpan.FromSeconds(2), ct);
            if (!string.Equals(Text(identity, "service"), "teletool", StringComparison.OrdinalIgnoreCase))
                return null;

            var configTask = GetAsync(ipAddress, port, "/api/config/ui", TimeSpan.FromSeconds(3), ct);
            var statusTask = GetAsync(ipAddress, port, "/api/status?lite=1&stats=1&logs=0&rf=1", TimeSpan.FromSeconds(4), ct);
            var networkTask = GetAsync(ipAddress, port, "/api/system/network_info", TimeSpan.FromSeconds(3), ct);
            var audioStatusTask = TryGetAsync(ipAddress, port, "/api/audio/status?logs=0", TimeSpan.FromSeconds(3), ct);
            var audioDevicesTask = TryGetAsync(ipAddress, port, "/api/audio/devices", TimeSpan.FromSeconds(3), ct);
            await Task.WhenAll(
                (Task)configTask,
                statusTask,
                networkTask,
                audioStatusTask,
                audioDevicesTask);

            var config = await configTask;
            var status = await statusTask;
            var network = await networkTask;
            var audioStatus = await audioStatusTask;
            var audioDevices = await audioDevicesTask;
            var release = Object(identity, "release");
            var adoption = Object(identity, "adoption");
            var remoteManager = Object(identity, "manager");
            var manager = await fleetIdentity.GetAsync(ct);
            var remoteManagerId = Text(adoption, "manager_id");
            var adopted = Flag(adoption, "adopted");
            var managedCount = Number(remoteManager, "managed_count", 0);

            string managementState;
            string managementMessage;
            var canAdopt = true;
            if (managedCount > 0)
            {
                managementState = "primary";
                managementMessage = $"Primary managing {managedCount} TeleTool unit{(managedCount == 1 ? "" : "s")}";
                canAdopt = false;
            }
            else if (adopted && !string.Equals(remoteManagerId, manager.ManagerId, StringComparison.Ordinal))
            {
                managementState = "adopted-other";
                managementMessage = $"Adopted by {Text(adoption, "manager_name") ?? Text(adoption, "manager_url") ?? "another Fleet Manager"}";
                canAdopt = false;
            }
            else if (adopted)
            {
                managementState = "recoverable";
                managementMessage = "Already adopted by this configurator";
            }
            else
            {
                managementState = "available";
                managementMessage = "Available for onboarding";
            }

            var capabilityReady = HasDevOnboardingFields(config);
            if (!capabilityReady)
            {
                canAdopt = false;
                managementState = "update-required";
                managementMessage = $"Update TeleTool from its Dev channel to {RequiredDevVersion} or later";
            }

            var mac = NormaliseMac(Text(identity, "mac_address") ?? Text(identity, "device_id"));
            var id = mac is null
                ? $"TT-{ipAddress.Replace('.', '-')}"
                : $"TT-{mac.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant()}";
            var device = new ManagedDevice
            {
                Id = id,
                IpAddress = ipAddress,
                MacAddress = mac ?? "unknown",
                Hostname = Text(identity, "hostname") ?? ipAddress,
                Model = "TeleTool",
                Family = DeviceFamily.TeleTool,
                Role = DeviceRole.Encoder,
                Health = DeviceHealth.Online,
                WebPort = port,
                CanOnboard = canAdopt,
                ManagementState = managementState,
                ManagementMessage = managementMessage,
                FirmwareVersion = Text(release, "version"),
                TeleToolReleaseBranch = Text(release, "branch"),
                IsStatic = string.Equals(Text(Object(network, "network"), "mode"), "manual", StringComparison.OrdinalIgnoreCase),
                LicenseAccepted = true
            };
            return ApplyStatus(device, status, config, release, null, audioStatus, audioDevices);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogDebug(ex, "TeleTool probe failed for {Address}:{Port}", ipAddress, port);
            return null;
        }
    }

    public async Task<ManagedDevice> ReadAsync(ManagedDevice device, bool adopt, CancellationToken ct)
    {
        if (device.Family == DeviceFamily.SimulatedTeleTool)
            return device with { Health = DeviceHealth.Online, LastSeenUtc = DateTimeOffset.UtcNow, LastError = null };

        if (!adopt)
        {
            var probed = await ProbeAsync(device.IpAddress, device.WebPort, ct)
                ?? throw new HttpRequestException($"TeleTool did not respond at {device.IpAddress}:{device.WebPort}.");
            return probed with
            {
                Id = device.Id,
                IsOnboarded = device.IsOnboarded,
                IsStatic = device.IsStatic || probed.IsStatic,
                NdiGroup = string.IsNullOrWhiteSpace(probed.NdiGroup) ? device.NdiGroup : probed.NdiGroup,
                NdiChannelName = string.IsNullOrWhiteSpace(probed.NdiChannelName) ? device.NdiChannelName : probed.NdiChannelName
            };
        }

        var gate = _adoptionGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var tracked = (await store.ReadAsync()).Devices.FirstOrDefault(candidate => candidate.Id == device.Id);
            if (tracked?.IsOnboarded != true)
                return await ReadAsync((tracked ?? device) with { IsOnboarded = false }, false, ct);

            device = tracked;
            var manager = await fleetIdentity.GetAsync(ct);
            var snapshotTask = PostAsync(device.IpAddress, device.WebPort, "/api/manager/snapshot", new
            {
                manager_id = manager.ManagerId,
                manager_url = manager.ManagerUrl,
                manager_name = manager.ManagerName,
                heartbeat = true
            }, TimeSpan.FromSeconds(8), ct);
            var configTask = GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
            var audioStatusTask = TryGetAsync(device.IpAddress, device.WebPort, "/api/audio/status?logs=0", TimeSpan.FromSeconds(3), ct);
            var audioDevicesTask = TryGetAsync(device.IpAddress, device.WebPort, "/api/audio/devices", TimeSpan.FromSeconds(3), ct);
            await Task.WhenAll(
                (Task)snapshotTask,
                configTask,
                audioStatusTask,
                audioDevicesTask);
            var snapshot = await snapshotTask;
            var config = await configTask;
            var status = Object(snapshot, "status");
            var release = Object(snapshot, "release");
            var host = Object(snapshot, "hostname");
            var adoption = Object(snapshot, "adoption");
            var hostname = Text(host, "hostname");
            if (!string.IsNullOrWhiteSpace(hostname)) device = device with { Hostname = hostname };
            return ApplyStatus(device, status, config, release, adoption, await audioStatusTask, await audioDevicesTask) with
            {
                IsOnboarded = true,
                IsStatic = device.IsStatic
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ConfigureAsync(
        ManagedDevice device,
        string hostname,
        string ndiName,
        string ndiGroup,
        string discoveryServer,
        CancellationToken ct)
    {
        if (device.Family == DeviceFamily.SimulatedTeleTool)
        {
            await ChangeSimulationAsync(device.Id, current => current with
            {
                Hostname = hostname,
                NdiChannelName = ndiName,
                NdiGroup = ndiGroup,
                Role = DeviceRole.Encoder,
                CanOnboard = true,
                ManagementState = "managed",
                ManagementMessage = "Managed by this configurator"
            });
            return;
        }

        var config = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
        if (!HasDevOnboardingFields(config))
            throw new InvalidOperationException($"TeleTool {RequiredDevVersion} or later from the Dev branch is required for NDI group and Discovery Server onboarding.");

        var status = await GetAsync(device.IpAddress, device.WebPort, "/api/status?lite=1&rf=0", TimeSpan.FromSeconds(5), ct);
        await PostAsync(device.IpAddress, device.WebPort, "/api/system/hostname", new { hostname }, TimeSpan.FromSeconds(10), ct);
        var updatedConfig = await PostAsync(device.IpAddress, device.WebPort, "/api/config/ui", new
        {
            ndi_default_name = ndiName,
            ndi_groups = ndiGroup,
            ndi_discovery_server = discoveryServer
        }, TimeSpan.FromSeconds(8), ct);

        var applied = Object(updatedConfig, "config");
        if (!string.Equals(Text(applied, "ndi_default_name"), ndiName, StringComparison.Ordinal)
            || !string.Equals(Text(applied, "ndi_groups"), ndiGroup, StringComparison.Ordinal)
            || !string.Equals(Text(applied, "ndi_discovery_server"), discoveryServer, StringComparison.Ordinal))
            throw new InvalidOperationException("TeleTool did not retain the requested NDI identity, group, and Discovery Server settings.");

        if (Flag(status, "running"))
        {
            if (IsTestCard(status))
            {
                var start = BuildStreamSettings(status, applied, ndiName, ndiGroup);
                await PostAsync(device.IpAddress, device.WebPort, "/api/test-card/stop", new { }, TimeSpan.FromSeconds(10), ct);
                await PostAsync(device.IpAddress, device.WebPort, "/api/test-card/start", start, TimeSpan.FromSeconds(20), ct);
            }
            else if (ChannelUuid(status) is not null)
            {
                var start = BuildStartPayload(status, applied, ndiName, ndiGroup);
                await PostAsync(device.IpAddress, device.WebPort, "/api/start", start, TimeSpan.FromSeconds(20), ct);
            }
            // A stopped/unselected TeleTool is still fully onboarded. Its saved
            // identity and discovery settings apply when an operator later
            // chooses a TV channel and starts the stream.
        }
    }

    public async Task SetNetworkAsync(ManagedDevice device, string address, string mask, string gateway, string dns, CancellationToken ct)
    {
        if (device.Family == DeviceFamily.SimulatedTeleTool)
        {
            await ChangeSimulationAsync(device.Id, current => current with { IpAddress = address, IsStatic = true });
            return;
        }

        try
        {
            await PostAsync(device.IpAddress, device.WebPort, "/api/system/network", new
            {
                mode = "manual",
                ip_address = address,
                subnet_mask = mask,
                gateway = string.IsNullOrWhiteSpace(gateway) ? null : gateway,
                dns
            }, TimeSpan.FromSeconds(30), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // NetworkManager may move eth0 before the response reaches the old
            // address. Treat that disconnect as success only when the TeleTool
            // discovery identity appears at the requested target address.
            if (!await WaitForIdentityAsync(address, device.WebPort, TimeSpan.FromSeconds(45), ct)) throw;
        }
    }

    public async Task SetIdentityAsync(ManagedDevice device, string hostname, string ndiName, string group, CancellationToken ct)
    {
        if (device.Family == DeviceFamily.SimulatedTeleTool)
        {
            await ConfigureAsync(device, hostname, ndiName, group, "", ct);
            return;
        }
        var config = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
        await ConfigureAsync(device, hostname, ndiName, group, Text(config, "ndi_discovery_server") ?? "", ct);
    }

    public async Task ConfigureMulticastAsync(
        ManagedDevice device,
        MulticastDeviceConfiguration settings,
        CancellationToken ct)
    {
        if (settings.NetPrefix is null || settings.Netmask is null)
            throw new ArgumentException("A TeleTool encoder requires a multicast prefix and subnet mask.");
        if (device.Family == DeviceFamily.SimulatedTeleTool)
        {
            await ChangeSimulationAsync(device.Id, current => current with
            {
                MulticastConfigured = true,
                MulticastInUse = current.StreamRunning == true,
                MulticastNetPrefix = settings.NetPrefix,
                MulticastNetmask = settings.Netmask,
                MulticastTtl = settings.Ttl,
                MulticastLastError = null
            });
            return;
        }

        var gate = _adoptionGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var config = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
            if (!HasDevMulticastFields(config))
                throw new InvalidOperationException("This TeleTool Dev build does not expose NDI multicast configuration. Update it from the TeleTool Dev channel first.");
            var status = await GetAsync(device.IpAddress, device.WebPort, "/api/status?lite=1&rf=0", TimeSpan.FromSeconds(5), ct);
            var wasRunning = Flag(status, "running");
            JsonObject? applied = null;
            try
            {
                if (wasRunning) await StopForConfigurationAsync(device, status, ct);
                var updated = await PostAsync(device.IpAddress, device.WebPort, "/api/config/ui", new
                {
                    ndi_multicast_enabled = true,
                    ndi_multicast_netprefix = settings.NetPrefix,
                    ndi_multicast_netmask = settings.Netmask,
                    ndi_multicast_ttl = settings.Ttl
                }, TimeSpan.FromSeconds(12), ct);
                applied = Object(updated, "config");
                if (!MatchesMulticast(applied, settings))
                    throw new InvalidOperationException("TeleTool did not retain the requested NDI multicast allocation.");

                if (wasRunning)
                {
                    var start = BuildPreviousSourcePayload(status, applied, device.NdiChannelName, device.NdiGroup);
                    ApplyMulticastSettings(start.Payload, settings);
                    await PostAsync(device.IpAddress, device.WebPort, start.Path, start.Payload, TimeSpan.FromSeconds(25), ct);
                    await ConfirmRunningMulticastAsync(device, settings, ct);
                }
            }
            catch (Exception primary) when (wasRunning)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await ThrowWithRestoreFailureAsync(
                    primary,
                    () => RestorePreviousSourceAsync(device, status, applied ?? config, recovery.Token));
                throw;
            }

            var retained = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
            if (!MatchesMulticast(retained, settings))
                throw new InvalidOperationException("TeleTool multicast configuration changed after the stream restart.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DisableMulticastAsync(ManagedDevice device, CancellationToken ct)
    {
        if (device.Family == DeviceFamily.SimulatedTeleTool)
        {
            await ChangeSimulationAsync(device.Id, current => current with
            {
                MulticastConfigured = false,
                MulticastInUse = false,
                MulticastNetPrefix = null,
                MulticastNetmask = null,
                MulticastTtl = null,
                MulticastLastError = null
            });
            return;
        }

        var gate = _adoptionGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var config = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
            if (!HasDevMulticastFields(config))
                throw new InvalidOperationException("This TeleTool Dev build does not expose NDI multicast configuration. Update it from the TeleTool Dev channel first.");
            var status = await GetAsync(device.IpAddress, device.WebPort, "/api/status?lite=1&rf=0", TimeSpan.FromSeconds(5), ct);
            var wasRunning = Flag(status, "running");
            JsonObject? applied = null;
            try
            {
                if (wasRunning) await StopForConfigurationAsync(device, status, ct);
                var updated = await PostAsync(device.IpAddress, device.WebPort, "/api/config/ui", new
                {
                    ndi_multicast_enabled = false
                }, TimeSpan.FromSeconds(12), ct);
                applied = Object(updated, "config");
                if (Flag(applied, "ndi_multicast_enabled"))
                    throw new InvalidOperationException("TeleTool did not retain unicast mode.");

                if (wasRunning)
                {
                    var start = BuildPreviousSourcePayload(status, applied, device.NdiChannelName, device.NdiGroup);
                    start.Payload["ndi_multicast_enabled"] = false;
                    await PostAsync(device.IpAddress, device.WebPort, start.Path, start.Payload, TimeSpan.FromSeconds(25), ct);
                    await ConfirmRunningUnicastAsync(device, ct);
                }
            }
            catch (Exception primary) when (wasRunning)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await ThrowWithRestoreFailureAsync(
                    primary,
                    () => RestorePreviousSourceAsync(device, status, applied ?? config, recovery.Token));
                throw;
            }

            var retained = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
            if (Flag(retained, "ndi_multicast_enabled"))
                throw new InvalidOperationException("TeleTool returned to multicast mode after the stream restart.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ManagedDevice> StartAsync(ManagedDevice device, CancellationToken ct)
    {
        if (!device.IsTeleTool()) throw new InvalidOperationException("Only TeleTool encoders support TeleTool fleet controls.");
        if (!device.IsOnboarded) throw new InvalidOperationException("Onboard this TeleTool before using fleet controls.");
        if (device.Family == DeviceFamily.SimulatedTeleTool)
        {
            await ChangeSimulationAsync(device.Id, current => current with
            {
                StreamRunning = true,
                StreamStatus = "running",
                PipelineStatus = "healthy",
                ActiveChannelName = current.ActiveChannelName ?? "Simulation channel",
                TeleToolControlReady = true,
                Health = DeviceHealth.Online,
                LastError = null
            });
            return (await store.ReadAsync()).Devices.First(d => d.Id == device.Id);
        }

        var status = await GetAsync(device.IpAddress, device.WebPort, "/api/status?lite=1&rf=1", TimeSpan.FromSeconds(6), ct);
        var config = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
        if (!Flag(status, "running"))
        {
            var start = BuildStartPayload(status, config, device.NdiChannelName, device.NdiGroup);
            await PostAsync(device.IpAddress, device.WebPort, "/api/start", start, TimeSpan.FromSeconds(20), ct);
        }
        return await ReadAsync(device, true, ct);
    }

    public async Task<ManagedDevice> StopAsync(ManagedDevice device, CancellationToken ct)
    {
        if (!device.IsTeleTool()) throw new InvalidOperationException("Only TeleTool encoders support TeleTool fleet controls.");
        if (!device.IsOnboarded) throw new InvalidOperationException("Onboard this TeleTool before using fleet controls.");
        if (device.Family == DeviceFamily.SimulatedTeleTool)
        {
            await ChangeSimulationAsync(device.Id, current => current with
            {
                StreamRunning = false,
                StreamStatus = "stopped",
                PipelineStatus = "stopped",
                ActiveChannelName = null,
                Health = DeviceHealth.Online,
                LastError = null
            });
            return (await store.ReadAsync()).Devices.First(d => d.Id == device.Id);
        }

        await PostAsync(device.IpAddress, device.WebPort, "/api/stop", new { }, TimeSpan.FromSeconds(15), ct);
        return await ReadAsync(device, true, ct);
    }

    public async Task<TeleToolRemovalResult> RemoveAsync(ManagedDevice device, CancellationToken ct)
    {
        if (!device.IsTeleTool()) throw new InvalidOperationException("Only TeleTool encoders can be removed from the TeleTool fleet.");
        if (!device.IsOnboarded) throw new InvalidOperationException("This TeleTool is not onboarded.");

        var gate = _adoptionGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync();
            var tracked = state.Devices.FirstOrDefault(candidate => candidate.Id == device.Id)
                ?? throw new KeyNotFoundException($"Device '{device.Id}' was not found.");
            if (!tracked.IsOnboarded) throw new InvalidOperationException("This TeleTool is not onboarded.");

            if (tracked.Family != DeviceFamily.SimulatedTeleTool)
            {
                var manager = await fleetIdentity.GetAsync(ct);
                var response = await PostAsync(tracked.IpAddress, tracked.WebPort, "/api/manager/adoption/release", new
                {
                    manager_id = manager.ManagerId
                }, TimeSpan.FromSeconds(8), ct);
                var adoption = Object(response, "adoption");
                if (Flag(adoption, "adopted"))
                {
                    var owner = Text(adoption, "manager_name") ?? Text(adoption, "manager_url") ?? "another Fleet Manager";
                    throw new InvalidOperationException($"TeleTool is still adopted by {owner}; it was not removed from this job.");
                }
            }

            var updated = await store.UpdateAsync(current => current with
            {
                Devices = current.Devices.Where(candidate => candidate.Id != tracked.Id).ToArray()
            });
            var remaining = updated.Devices.Count(candidate => candidate.IsOnboarded && candidate.IsTeleTool());
            logger.LogInformation("Removed TeleTool {DeviceId} at {Address} from the onboarded fleet", tracked.Id, tracked.IpAddress);
            return new(tracked.Id, tracked.Hostname, remaining, "released");
        }
        finally
        {
            gate.Release();
            if (_adoptionGates.TryGetValue(device.Id, out var current) && ReferenceEquals(current, gate))
                _adoptionGates.TryRemove(device.Id, out _);
        }
    }

    private async Task ChangeSimulationAsync(string id, Func<ManagedDevice, ManagedDevice> update) =>
        await store.UpdateAsync(state => state with
        {
            Devices = state.Devices.Select(device => device.Id == id ? update(device) : device).ToArray()
        });

    private async Task<bool> WaitForIdentityAsync(string address, int port, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var identity = await GetAsync(address, port, "/api/manager/discovery", TimeSpan.FromSeconds(2), ct);
                if (string.Equals(Text(identity, "service"), "teletool", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return false;
    }

    private static ManagedDevice ApplyStatus(
        ManagedDevice device,
        JsonObject status,
        JsonObject config,
        JsonObject release,
        JsonObject? adoption,
        JsonObject? audioStatus,
        JsonObject? audioDevices)
    {
        var supervisor = Object(status, "supervisor");
        var lastStart = Object(supervisor, "last_start_request");
        var running = Flag(status, "running");
        var activeName = Text(status, "active_channel_name");
        var activeNumber = Text(status, "active_channel_number");
        var rf = Object(status, "rf");
        var rfSignal = RfLabel(rf);
        var rfKind = RfKind(rf);
        var systemTemperatureC = DecimalNumber(status, "system_temperature_c");
        var adoptionOk = adoption is null || Flag(adoption, "ok", true);
        var error = Text(status, "last_error") ?? Text(supervisor, "last_error");
        if (!adoptionOk) error = "Adopted by another active TeleTool Fleet Manager.";
        var ndiName = Text(status, "ndi_name")
            ?? Text(supervisor, "desired_ndi_name")
            ?? Text(config, "ndi_default_name")
            ?? device.NdiChannelName;
        var groups = Text(config, "ndi_groups");
        var controlReady = FirstText(
            Text(status, "channel_uuid"),
            Text(status, "active_channel_uuid"),
            Text(supervisor, "desired_channel_uuid"),
            Text(lastStart, "channel_uuid")) is not null;
        var dante = ReadDanteAudio(audioStatus, audioDevices);
        var multicastConfigured = Flag(config, "ndi_multicast_enabled");
        var multicastActive = running && Flag(status, "ndi_multicast_enabled", multicastConfigured);
        var multicastPrefix = Text(status, "ndi_multicast_netprefix")
            ?? Text(status, "ndi_multicast_addr")
            ?? Text(config, "ndi_multicast_netprefix")
            ?? Text(config, "ndi_multicast_addr");
        var multicastMask = Text(status, "ndi_multicast_netmask") ?? Text(config, "ndi_multicast_netmask");
        var multicastTtl = Number(status, "ndi_multicast_ttl", Number(config, "ndi_multicast_ttl", 0));

        return device with
        {
            Role = DeviceRole.Encoder,
            Health = adoptionOk ? DeviceHealth.Online : DeviceHealth.Error,
            LastSeenUtc = DateTimeOffset.UtcNow,
            LastError = error,
            NdiChannelName = ndiName,
            NdiGroup = string.IsNullOrWhiteSpace(groups) ? device.NdiGroup : groups,
            FirmwareVersion = Text(release, "version") ?? device.FirmwareVersion,
            TeleToolReleaseBranch = Text(release, "branch") ?? device.TeleToolReleaseBranch,
            StreamRunning = running,
            StreamStatus = running ? "running" : "stopped",
            ActiveChannelName = activeName,
            ActiveChannelNumber = activeNumber,
            PipelineStatus = Text(supervisor, "pipeline_status") ?? Text(status, "pipeline_state"),
            RfSignal = rfSignal,
            RfSignalKind = rfKind,
            SystemTemperatureC = systemTemperatureC,
            DanteAudioActive = dante?.Active,
            DanteAudioReady = dante?.Ready,
            DanteAudioStatus = dante?.Status,
            DanteAudioDeviceLabel = dante?.DeviceLabel,
            DanteAudioDetails = dante?.Details,
            DanteAudioKind = dante?.Kind,
            MulticastConfigured = multicastConfigured,
            MulticastInUse = multicastConfigured && multicastActive,
            MulticastNetPrefix = multicastConfigured ? multicastPrefix : device.MulticastNetPrefix,
            MulticastNetmask = multicastConfigured ? multicastMask : device.MulticastNetmask,
            MulticastTtl = multicastConfigured && multicastTtl > 0 ? multicastTtl : device.MulticastTtl,
            MulticastLastError = null,
            TeleToolControlReady = controlReady,
            ManagementState = adoption is null ? device.ManagementState : adoptionOk ? "managed" : "adopted-other",
            ManagementMessage = adoption is null ? device.ManagementMessage : adoptionOk ? "Managed by this configurator" : "Adopted by another Fleet Manager"
        };
    }

    private static DanteAudioSnapshot? ReadDanteAudio(JsonObject? audioStatus, JsonObject? audioDevices)
    {
        if (audioStatus is null && audioDevices is null) return null;

        var devices = (audioDevices?["devices"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(IsDanteAudioDevice)
            .ToArray() ?? [];
        var statusDeviceId = Text(audioStatus, "device_id");
        var selectedId = FirstText(statusDeviceId, Text(audioDevices, "selected"));
        var device = devices.FirstOrDefault(candidate =>
                string.Equals(Text(candidate, "id"), selectedId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault();

        if (device is null)
            return new(false, false, "Unavailable", null, "No Dante-compatible audio output was detected.", null);

        var deviceId = Text(device, "id");
        var label = Text(device, "label") ?? "Dante-compatible audio output";
        var ready = Flag(device, "ready", true);
        var running = Flag(audioStatus, "running");
        var active = running && (statusDeviceId is null ||
            string.Equals(statusDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        var error = Text(audioStatus, "last_error");
        var details = Text(device, "details");
        var status = active ? "Active" : !ready ? "Fault" : error is not null ? "Error" : "Off";
        var message = active
            ? $"{label} audio output is running."
            : FirstText(error, details, ready
                ? $"{label} audio output is ready but stopped."
                : $"{label} is not ready.")!;

        return new(active, ready, status, label, message, Text(device, "kind"));
    }

    private static bool IsDanteAudioDevice(JsonObject device)
    {
        var identity = string.Join(" ", Text(device, "id"), Text(device, "label"), Text(device, "kind"), Text(device, "details"));
        return new[] { "dante", "avio", "audinate", "inferno" }
            .Any(term => identity.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, object?> BuildStartPayload(
        JsonObject status,
        JsonObject config,
        string ndiName,
        string ndiGroups)
    {
        var channelUuid = ChannelUuid(status);
        if (channelUuid is null)
            throw new InvalidOperationException("Open this TeleTool UI and choose a TV channel before starting NDI from the configurator.");

        var result = BuildStreamSettings(status, config, ndiName, ndiGroups);
        result["channel_uuid"] = channelUuid;
        return result;
    }

    private static (string Path, Dictionary<string, object?> Payload) BuildPreviousSourcePayload(
        JsonObject status,
        JsonObject config,
        string ndiName,
        string ndiGroups) => IsTestCard(status)
            ? ("/api/test-card/start", BuildStreamSettings(status, config, ndiName, ndiGroups))
            : ("/api/start", BuildStartPayload(status, config, ndiName, ndiGroups));

    private async Task StopForConfigurationAsync(
        ManagedDevice device,
        JsonObject status,
        CancellationToken ct)
    {
        var path = IsTestCard(status) ? "/api/test-card/stop" : "/api/stop";
        await PostAsync(device.IpAddress, device.WebPort, path, new { }, TimeSpan.FromSeconds(15), ct);
        await ConfirmStoppedAsync(device, ct);
    }

    private async Task RestorePreviousSourceAsync(
        ManagedDevice device,
        JsonObject previousStatus,
        JsonObject config,
        CancellationToken ct)
    {
        var current = await GetAsync(
            device.IpAddress,
            device.WebPort,
            "/api/status?lite=1&rf=0",
            TimeSpan.FromSeconds(5),
            ct);
        if (Flag(current, "running")) return;

        var start = BuildPreviousSourcePayload(
            previousStatus,
            config,
            device.NdiChannelName,
            device.NdiGroup);
        await PostAsync(device.IpAddress, device.WebPort, start.Path, start.Payload, TimeSpan.FromSeconds(25), ct);
    }

    private static async Task ThrowWithRestoreFailureAsync(
        Exception primary,
        Func<Task> restore)
    {
        try
        {
            await restore();
        }
        catch (Exception restoreError)
        {
            throw new InvalidOperationException(
                $"{primary.Message} The previous TeleTool source could not be restored: {restoreError.Message}",
                new AggregateException(primary, restoreError));
        }
    }

    private static Dictionary<string, object?> BuildStreamSettings(
        JsonObject status,
        JsonObject config,
        string ndiName,
        string ndiGroups)
    {
        var supervisor = Object(status, "supervisor");
        var lastStart = Object(supervisor, "last_start_request");

        return new()
        {
            ["ndi_name"] = ndiName,
            ["ndi_groups"] = ndiGroups,
            ["profile"] = FirstText(Text(supervisor, "desired_profile"), Text(lastStart, "profile"), Text(status, "active_profile"), Text(config, "tvh_stream_profile")) ?? "pass",
            ["deinterlace"] = Flag(lastStart, "deinterlace", Flag(config, "ndi_deinterlace")),
            ["buffer_extra_ms"] = Number(lastStart, "buffer_extra_ms", Number(config, "ndi_buffer_extra_ms", 0)),
            ["ndi_qos"] = Flag(lastStart, "ndi_qos", Flag(config, "ndi_qos")),
            ["ndi_multicast_enabled"] = Flag(lastStart, "ndi_multicast_enabled", Flag(config, "ndi_multicast_enabled")),
            ["ndi_multicast_netprefix"] = Text(lastStart, "ndi_multicast_netprefix")
                ?? Text(lastStart, "ndi_multicast_addr")
                ?? Text(config, "ndi_multicast_netprefix")
                ?? Text(config, "ndi_multicast_addr")
                ?? "",
            ["ndi_multicast_netmask"] = Text(lastStart, "ndi_multicast_netmask")
                ?? Text(config, "ndi_multicast_netmask")
                ?? "255.255.0.0",
            ["ndi_multicast_addr"] = Text(lastStart, "ndi_multicast_netprefix")
                ?? Text(lastStart, "ndi_multicast_addr")
                ?? Text(config, "ndi_multicast_netprefix")
                ?? Text(config, "ndi_multicast_addr")
                ?? "",
            ["ndi_multicast_ttl"] = Number(lastStart, "ndi_multicast_ttl", Number(config, "ndi_multicast_ttl", 1))
        };
    }

    private static string? ChannelUuid(JsonObject status)
    {
        var supervisor = Object(status, "supervisor");
        var lastStart = Object(supervisor, "last_start_request");
        return FirstText(
            Text(status, "channel_uuid"),
            Text(status, "active_channel_uuid"),
            Text(supervisor, "desired_channel_uuid"),
            Text(lastStart, "channel_uuid"));
    }

    private static bool IsTestCard(JsonObject status) =>
        string.Equals(Text(status, "source_mode"), "test_card", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Text(Object(status, "supervisor"), "source_mode"), "test_card", StringComparison.OrdinalIgnoreCase) ||
        (Text(status, "input_url")?.StartsWith("test-card:", StringComparison.OrdinalIgnoreCase) ?? false);

    private async Task ConfirmStoppedAsync(ManagedDevice device, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var status = await GetAsync(
                    device.IpAddress,
                    device.WebPort,
                    "/api/status?lite=1&rf=0",
                    TimeSpan.FromSeconds(5),
                    ct);
                if (!Flag(status, "running")) return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException or DeviceApiException or JsonException)
            {
                lastError = ex;
            }
            if (attempt < 7) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new InvalidOperationException(
            "TeleTool did not stop before its NDI multicast configuration was changed.",
            lastError);
    }

    private async Task ConfirmRunningMulticastAsync(
        ManagedDevice device,
        MulticastDeviceConfiguration settings,
        CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var status = await GetAsync(
                    device.IpAddress,
                    device.WebPort,
                    "/api/status?lite=1&rf=0",
                    TimeSpan.FromSeconds(5),
                    ct);
                if (Flag(status, "running") && MatchesMulticast(status, settings)) return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException or DeviceApiException or JsonException)
            {
                lastError = ex;
            }
            if (attempt < 7) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new InvalidOperationException(
            "TeleTool restarted, but live status did not confirm multicast transmission.",
            lastError);
    }

    private async Task ConfirmRunningUnicastAsync(ManagedDevice device, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var status = await GetAsync(
                    device.IpAddress,
                    device.WebPort,
                    "/api/status?lite=1&rf=0",
                    TimeSpan.FromSeconds(5),
                    ct);
                if (Flag(status, "running") && !Flag(status, "ndi_multicast_enabled")) return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException or DeviceApiException or JsonException)
            {
                lastError = ex;
            }
            if (attempt < 7) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new InvalidOperationException(
            "TeleTool restarted, but live status did not confirm unicast transmission.",
            lastError);
    }

    private static void ApplyMulticastSettings(
        IDictionary<string, object?> payload,
        MulticastDeviceConfiguration settings)
    {
        payload["ndi_multicast_enabled"] = true;
        payload["ndi_multicast_netprefix"] = settings.NetPrefix;
        payload["ndi_multicast_addr"] = settings.NetPrefix;
        payload["ndi_multicast_netmask"] = settings.Netmask;
        payload["ndi_multicast_ttl"] = settings.Ttl;
    }

    private static bool MatchesMulticast(JsonObject source, MulticastDeviceConfiguration settings) =>
        Flag(source, "ndi_multicast_enabled")
        && string.Equals(
            Text(source, "ndi_multicast_netprefix") ?? Text(source, "ndi_multicast_addr"),
            settings.NetPrefix,
            StringComparison.Ordinal)
        && string.Equals(Text(source, "ndi_multicast_netmask"), settings.Netmask, StringComparison.Ordinal)
        && Number(source, "ndi_multicast_ttl", 0) == settings.Ttl;

    private async Task<JsonObject> GetAsync(
        string address,
        int port,
        string path,
        TimeSpan timeout,
        CancellationToken ct) =>
        await SendAsync(address, port, HttpMethod.Get, path, null, timeout, ct);

    private async Task<JsonObject?> TryGetAsync(
        string address,
        int port,
        string path,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            return await GetAsync(address, port, path, timeout, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            (ex is HttpRequestException or TaskCanceledException or JsonException))
        {
            logger.LogDebug(ex, "Optional TeleTool endpoint {Path} unavailable for {Address}:{Port}", path, address, port);
            return null;
        }
    }

    private async Task<JsonObject> PostAsync(
        string address,
        int port,
        string path,
        object body,
        TimeSpan timeout,
        CancellationToken ct) =>
        await SendAsync(address, port, HttpMethod.Post, path, body, timeout, ct);

    private async Task<JsonObject> SendAsync(
        string address,
        int port,
        HttpMethod method,
        string path,
        object? body,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, new Uri($"http://{address}:{port}{path}"));
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("NDI-Job-Configurator/TeleTool-Fleet");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await clients.CreateClient("TeleTool").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        var text = await response.Content.ReadAsStringAsync(linked.Token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryDetail(text);
            throw new HttpRequestException(
                detail ?? $"TeleTool {path} returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        if (string.IsNullOrWhiteSpace(text)) return new();
        return JsonNode.Parse(text) as JsonObject
            ?? throw new JsonException($"TeleTool {path} returned an unexpected JSON payload.");
    }

    private static string? TryDetail(string text)
    {
        try { return Text(JsonNode.Parse(text) as JsonObject, "detail"); }
        catch (JsonException) { return null; }
    }

    private static bool HasDevOnboardingFields(JsonObject config) =>
        config.ContainsKey("ndi_groups") && config.ContainsKey("ndi_discovery_server");

    private static bool HasDevMulticastFields(JsonObject config) =>
        config.ContainsKey("ndi_multicast_enabled")
        && (config.ContainsKey("ndi_multicast_netprefix") || config.ContainsKey("ndi_multicast_addr"))
        && config.ContainsKey("ndi_multicast_netmask")
        && config.ContainsKey("ndi_multicast_ttl");

    private static JsonObject Object(JsonObject? source, string name) => source?[name] as JsonObject ?? new();

    private static string? Text(JsonObject? source, string name)
    {
        if (source?[name] is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (value.TryGetValue<int>(out var number)) return number.ToString();
        if (value.TryGetValue<long>(out var longNumber)) return longNumber.ToString();
        return null;
    }

    private static bool Flag(JsonObject? source, string name, bool fallback = false)
    {
        if (source?[name] is not JsonValue value) return fallback;
        if (value.TryGetValue<bool>(out var result)) return result;
        if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out result)) return result;
        return fallback;
    }

    private static int Number(JsonObject? source, string name, int fallback)
    {
        if (source?[name] is not JsonValue value) return fallback;
        if (value.TryGetValue<int>(out var result)) return result;
        if (value.TryGetValue<long>(out var longResult) && longResult is >= int.MinValue and <= int.MaxValue) return (int)longResult;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out result)) return result;
        return fallback;
    }

    private static double? DecimalNumber(JsonObject? source, string name)
    {
        if (source?[name] is not JsonValue value) return null;
        double result;
        if (!value.TryGetValue<double>(out result))
        {
            if (value.TryGetValue<decimal>(out var decimalResult)) result = (double)decimalResult;
            else if (!value.TryGetValue<string>(out var text)
                || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return null;
        }

        return double.IsFinite(result) && result is >= -40 and <= 150
            ? Math.Round(result, 1)
            : null;
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private sealed record DanteAudioSnapshot(
        bool Active,
        bool Ready,
        string Status,
        string? DeviceLabel,
        string Details,
        string? Kind);

    private static string? NormaliseMac(string? value)
    {
        var text = value?.Trim().ToLowerInvariant().Replace('-', ':');
        if (text?.StartsWith("mac:", StringComparison.Ordinal) == true) text = text[4..];
        return text is not null && System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-f]{2}(?::[0-9a-f]{2}){5}$")
            ? text
            : null;
    }

    private static string? RfLabel(JsonObject rf)
    {
        if (!Flag(rf, "available")) return "N/A";
        var label = Text(rf, "dbm_label");
        if (!string.IsNullOrWhiteSpace(label) && !string.Equals(label, "N/A", StringComparison.OrdinalIgnoreCase)) return label;
        if (rf["dbm"] is JsonValue dbm && dbm.TryGetValue<double>(out var dbmValue)) return $"{Math.Round(dbmValue):0} dBm";
        if (rf["percent"] is JsonValue percent && percent.TryGetValue<double>(out var percentValue)) return $"{Math.Round(percentValue):0}%";
        return Text(rf, "label") ?? "N/A";
    }

    private static string RfKind(JsonObject rf)
    {
        var kind = Text(rf, "kind");
        if (kind is "good" or "warn" or "bad") return kind;
        if (rf["dbm"] is JsonValue dbm && dbm.TryGetValue<double>(out var dbmValue))
            return dbmValue >= -65 ? "good" : dbmValue >= -80 ? "warn" : "bad";
        if (rf["percent"] is JsonValue percent && percent.TryGetValue<double>(out var percentValue))
            return percentValue >= 65 ? "good" : percentValue >= 35 ? "warn" : "bad";
        return "bad";
    }
}
