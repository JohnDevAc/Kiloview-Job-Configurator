using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KiloviewSetup.Core;

public sealed class TeleToolFleetService(
    IHttpClientFactory clients,
    AppStateStore store,
    TeleToolFleetIdentity fleetIdentity,
    ILogger<TeleToolFleetService> logger)
{
    public const int DefaultPort = 8000;
    public const string RequiredDevVersion = "1.8.5+dev.54";

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
            await Task.WhenAll(configTask, statusTask, networkTask);

            var config = await configTask;
            var status = await statusTask;
            var network = await networkTask;
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
            return ApplyStatus(device, status, config, release, null);
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

        var manager = await fleetIdentity.GetAsync(ct);
        var snapshot = await PostAsync(device.IpAddress, device.WebPort, "/api/manager/snapshot", new
        {
            manager_id = manager.ManagerId,
            manager_url = manager.ManagerUrl,
            manager_name = manager.ManagerName,
            heartbeat = true
        }, TimeSpan.FromSeconds(8), ct);
        var config = await GetAsync(device.IpAddress, device.WebPort, "/api/config/ui", TimeSpan.FromSeconds(4), ct);
        var status = Object(snapshot, "status");
        var release = Object(snapshot, "release");
        var host = Object(snapshot, "hostname");
        var adoption = Object(snapshot, "adoption");
        var hostname = Text(host, "hostname");
        if (!string.IsNullOrWhiteSpace(hostname)) device = device with { Hostname = hostname };
        return ApplyStatus(device, status, config, release, adoption) with
        {
            IsOnboarded = device.IsOnboarded,
            IsStatic = device.IsStatic
        };
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
            var start = BuildStartPayload(status, applied, ndiName, ndiGroup);
            await PostAsync(device.IpAddress, device.WebPort, "/api/start", start, TimeSpan.FromSeconds(20), ct);
        }
    }

    public async Task SetNetworkAsync(ManagedDevice device, string address, string mask, string gateway, CancellationToken ct)
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
                dns = ""
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
        JsonObject? adoption)
    {
        var supervisor = Object(status, "supervisor");
        var lastStart = Object(supervisor, "last_start_request");
        var running = Flag(status, "running");
        var activeName = Text(status, "active_channel_name");
        var activeNumber = Text(status, "active_channel_number");
        var rf = Object(status, "rf");
        var rfSignal = RfLabel(rf);
        var rfKind = RfKind(rf);
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
            TeleToolControlReady = controlReady,
            ManagementState = adoption is null ? device.ManagementState : adoptionOk ? "managed" : "adopted-other",
            ManagementMessage = adoption is null ? device.ManagementMessage : adoptionOk ? "Managed by this configurator" : "Adopted by another Fleet Manager"
        };
    }

    private static Dictionary<string, object?> BuildStartPayload(
        JsonObject status,
        JsonObject config,
        string ndiName,
        string ndiGroups)
    {
        var supervisor = Object(status, "supervisor");
        var lastStart = Object(supervisor, "last_start_request");
        var channelUuid = FirstText(
            Text(status, "channel_uuid"),
            Text(status, "active_channel_uuid"),
            Text(supervisor, "desired_channel_uuid"),
            Text(lastStart, "channel_uuid"));
        if (channelUuid is null)
            throw new InvalidOperationException("Open this TeleTool UI and choose a TV channel before starting NDI from the configurator.");

        return new()
        {
            ["channel_uuid"] = channelUuid,
            ["ndi_name"] = ndiName,
            ["ndi_groups"] = ndiGroups,
            ["profile"] = FirstText(Text(supervisor, "desired_profile"), Text(lastStart, "profile"), Text(status, "active_profile"), Text(config, "tvh_stream_profile")) ?? "pass",
            ["deinterlace"] = Flag(lastStart, "deinterlace", Flag(config, "ndi_deinterlace")),
            ["buffer_extra_ms"] = Number(lastStart, "buffer_extra_ms", Number(config, "ndi_buffer_extra_ms", 0)),
            ["ndi_qos"] = Flag(lastStart, "ndi_qos", Flag(config, "ndi_qos")),
            ["ndi_multicast_enabled"] = Flag(lastStart, "ndi_multicast_enabled", Flag(config, "ndi_multicast_enabled")),
            ["ndi_multicast_addr"] = Text(lastStart, "ndi_multicast_addr") ?? Text(config, "ndi_multicast_addr") ?? "",
            ["ndi_multicast_ttl"] = Number(lastStart, "ndi_multicast_ttl", Number(config, "ndi_multicast_ttl", 1))
        };
    }

    private async Task<JsonObject> GetAsync(
        string address,
        int port,
        string path,
        TimeSpan timeout,
        CancellationToken ct) =>
        await SendAsync(address, port, HttpMethod.Get, path, null, timeout, ct);

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
        request.Headers.UserAgent.ParseAdd("Kiloview-Job-Configurator/TeleTool-Fleet");
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

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

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
