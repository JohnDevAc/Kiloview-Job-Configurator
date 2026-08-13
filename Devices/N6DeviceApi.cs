using System.Text.Json;
using System.Net.Http.Headers;
using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

internal sealed class N6DeviceApi(
    string ipAddress,
    DeviceCredentials credentials,
    IHttpClientFactory clients) : HttpDeviceApi(ipAddress, credentials, clients), IDeviceApi
{
    public async Task<ManagedDevice?> ProbeWebOnlyAsync(CancellationToken ct)
    {
        using var client = NewClient(TimeSpan.FromSeconds(8));
        using var login = await PostAsync(client, "/api/users/login.json",
            new { user = Credentials.Username, password = Credentials.Password },
            "probe N6 web-access state",
            ct);
        var data = login.RootElement.GetProperty("data");
        var firstLogin = data.TryGetProperty("changed", out var changed) && changed.ValueKind == JsonValueKind.False;

        return new ManagedDevice
        {
            Id = $"N6-FIRST-LOGIN-{IpAddress}",
            IpAddress = IpAddress,
            MacAddress = IpAddress,
            Hostname = firstLogin ? "N6 (first login)" : "N6 (API disabled)",
            Model = "N6",
            Family = DeviceFamily.N6,
            Role = DeviceRole.Unknown,
            Health = DeviceHealth.Online,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Credentials = Credentials,
            ManagementState = firstLogin ? "first-login" : "api-disabled",
            ManagementMessage = firstLogin
                ? "Factory N6 detected; onboarding will initialize its administrator password and API permission after confirmation."
                : "N6 Web login detected with HTTP API permission disabled; onboarding will enable it after confirmation."
        };
    }

    private async Task<HttpClient> AuthorizedAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        var client = NewClient(timeout ?? TimeSpan.FromSeconds(8));
        using var login = await PostAsync(client, "/api/user/authorize.json", new { user = Credentials.Username, password = Credentials.Password }, "N6 login", ct);
        var data = login.RootElement.GetProperty("data");
        var token = String(data, "token");
        var uri = new Uri($"http://{IpAddress}");
        Cookies.Add(uri, new System.Net.Cookie("username", Credentials.Username));
        Cookies.Add(uri, new System.Net.Cookie("user", Credentials.Username));
        Cookies.Add(uri, new System.Net.Cookie("alias", String(data, "alias", "Admin")));
        Cookies.Add(uri, new System.Net.Cookie("token", token));
        ApplyCookies(client);
        return client;
    }

    public async Task<ManagedDevice> ReadAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var version = await GetAsync(client, "/api/firmware/get.json", "read N6 version", ct);
        using var hostname = await GetAsync(client, "/api/device/get_hostname.json", "read N6 hostname", ct);
        using var network = await GetAsync(client, "/api/network/get.json", "read N6 network", ct);
        JsonDocument? mode = null;
        JsonDocument? codecStatus = null;
        string? modeWarning = null;
        try { mode = await GetAsync(client, "/api/mode/get.json", "read N6 mode", ct); }
        catch (DeviceApiException ex) when (IsModeServiceUnavailable(ex))
        {
            // The N6 web and network APIs can be healthy while its codec-mode
            // service is restarting. Keep the unit discoverable so onboarding
            // can run the guarded mode recovery path instead of losing it.
            modeWarning = "N6 mode service is not ready; role will be recovered during onboarding.";
        }
        try
        {
            codecStatus = await GetAsync(client, "/api/device/status.json?types=ndihx", "read N6 hardware identity", ct);
        }
        catch (DeviceApiException)
        {
            // Decoder mode and a restarting codec proxy can make the encoder
            // status endpoint unavailable. The existing hostname/MAC fallbacks
            // keep discovery working until a healthy status read supplies the
            // immutable hardware serial.
        }
        var net = network.RootElement.GetProperty("data")[0];
        var ver = version.RootElement.GetProperty("data");
        var hostnameText = String(hostname.RootElement.GetProperty("data"), "hostname", "N6");
        var serialFromHostname = hostnameText.StartsWith("N6-", StringComparison.OrdinalIgnoreCase)
            ? hostnameText[3..]
            : "";
        var serialFromCodec = codecStatus is null
            ? ""
            : String(Payload(codecStatus.RootElement), "serial_number").Trim();
        var serial = String(ver, "serialNumber", String(ver, "serial_number", string.IsNullOrWhiteSpace(serialFromCodec)
            ? serialFromHostname
            : serialFromCodec)).Trim();
        var mac = String(net, "mac", serial).Trim();
        if (string.IsNullOrWhiteSpace(serial)) serial = mac;
        var modeName = mode is null ? "" : String(mode.RootElement.GetProperty("data"), "mode");
        var result = new ManagedDevice
        {
            Id = string.IsNullOrWhiteSpace(serial) ? mac : serial,
            IpAddress = String(net, "ip", IpAddress),
            MacAddress = mac,
            Hostname = hostnameText,
            Model = "N6",
            Family = DeviceFamily.N6,
            FirmwareVersion = String(ver, "softwareVersion"),
            IsStatic = String(net, "dynamic") == "n",
            Role = modeName == "decoder" ? DeviceRole.Decoder : modeName == "encoder" ? DeviceRole.Encoder : DeviceRole.Unknown,
            Health = DeviceHealth.Online,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Credentials = Credentials,
            ManagementState = modeWarning is null ? null : "mode-recovery-required",
            ManagementMessage = modeWarning
        };
        codecStatus?.Dispose();
        mode?.Dispose();
        return result;
    }

    public async Task ProvisionAccessAsync(DeviceCredentials targetCredentials, CancellationToken ct)
    {
        using var web = NewClient(TimeSpan.FromSeconds(8));
        using var login = await PostAsync(web, "/api/users/login.json",
            new { user = Credentials.Username, password = Credentials.Password },
            "start N6 access provisioning",
            ct);
        var loginData = login.RootElement.GetProperty("data");
        var uri = new Uri($"http://{IpAddress}");
        Cookies.Add(uri, new System.Net.Cookie("user", Credentials.Username));
        Cookies.Add(uri, new System.Net.Cookie("alias", String(loginData, "alias", "Admin")));
        Cookies.Add(uri, new System.Net.Cookie("token", String(loginData, "token")));
        ApplyCookies(web);
        if (!string.Equals(Credentials.Username, targetCredentials.Username, StringComparison.Ordinal) ||
            !string.Equals(Credentials.Password, targetCredentials.Password, StringComparison.Ordinal))
        {
            using var changed = await PostAsync(web, "/api/users/modify.json", new
            {
                id = Credentials.Username,
                username = targetCredentials.Username,
                password = targetCredentials.Password,
                passwordAgain = targetCredentials.Password
            }, "set N6 onboarding credentials", ct);
        }

        // The mandatory first-login form only submits password fields. Re-authenticate
        // with the replacement credentials and enable API access in a separate user edit.
        using var permissions = NewClient(TimeSpan.FromSeconds(8));
        using var permissionLogin = await PostAsync(permissions, "/api/users/login.json",
            new { user = targetCredentials.Username, password = targetCredentials.Password },
            "re-authenticate N6 for API permission",
            ct);
        var permissionData = permissionLogin.RootElement.GetProperty("data");
        Cookies.Add(uri, new System.Net.Cookie("user", targetCredentials.Username));
        Cookies.Add(uri, new System.Net.Cookie("alias", String(permissionData, "alias", "Admin")));
        Cookies.Add(uri, new System.Net.Cookie("token", String(permissionData, "token")));
        ApplyCookies(permissions);
        using var apiPermission = await PostAsync(permissions, "/api/users/modify.json", new
        {
            id = targetCredentials.Username,
            alias = String(permissionData, "alias", "Admin"),
            web = true,
            api = true
        }, "enable N6 HTTP API permission", ct);

        var replacement = new N6DeviceApi(IpAddress, targetCredentials, Clients);
        using var verified = await replacement.AuthorizedAsync(ct);
        _ = await replacement.TryAcceptLicenseAsync(verified, ct);
    }

    public async Task UpdateFirmwareAsync(FirmwarePackage package, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct, TimeSpan.FromMinutes(20));
        await using var file = new FileStream(package.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "upload", package.FileName);
        form.Add(new StringContent(package.FileName), "path");
        ApplyCookies(client);
        using var response = await client.PostAsync("/api/firmware/upgrade.json", form, ct);
        using var accepted = await ReadJsonAsync(response, "upload N6 firmware", ct);
    }

    private async Task<bool> TryAcceptLicenseAsync(HttpClient client, CancellationToken ct)
    {
        var known = new[]
        {
            "/api/users/accept_eula.json",
            "/api/sys/accept_eula.json",
            "/api/sys/eula/accept.json",
            "/api/device/accept_eula.json",
            "/api/device/set_eula.json",
            "/api/license/accept.json"
        };
        var discovered = await FindFirstLoginApiPathsAsync(client, ct);
        foreach (var path in known.Concat(discovered).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await TryMutationAsync(client, path, new { accept = true, accepted = true, agree = true }, ct)) return true;
        }
        return false;
    }

    public async Task SetNetworkAsync(string address, string mask, string gateway, string dns, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var network = await GetAsync(client, "/api/network/get.json", "read N6 network", ct);
        var net = network.RootElement.GetProperty("data")[0];
        var device = String(net, "device", "eth0");
        try
        {
            using var _ = await PostAsync(client, "/api/network/modify.json", new { device, dynamic = "n", ip = address, mask, gw = gateway, dns }, "set N6 static address", ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* Expected while the address changes. */ }
        catch (HttpRequestException) { /* The caller verifies the device at its target address. */ }
    }

    public async Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        await ConfigureKiloLinkAsync(client, settings, ct);
        using var discovery = await PostWhenCodecReadyAsync(client, "/api/device/set_discovery_server.json",
            new { enable = true, servers = new[] { new { ip = settings.NdiDiscoveryServerIp, group_name = settings.JobName } } },
            "set N6 NDI discovery server",
            ct);
        foreach (var type in new[] { "ndihx", "ndifull" })
        {
            using var stream = await PostWhenCodecReadyAsync(client, "/api/device/modify.json",
                new { types = type, device_group = settings.JobName, channel_name = channelName, machine_name = hostname },
                $"set N6 {type} identity",
                ct);
        }
        using var host = await PostAsync(client, "/api/device/set_hostname.json", new { hostname }, "set N6 hostname", ct);
    }

    public async Task ConfigureDiscoveryServerAsync(string ipAddress, string group, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var configured = await PostWhenCodecReadyAsync(client, "/api/device/set_discovery_server.json",
            new { enable = true, servers = new[] { new { ip = ipAddress, group_name = group } } },
            "set N6 NDI discovery server after role selection",
            ct);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
            using var verified = await GetAsync(client, "/api/device/get_discovery_server.json", "verify N6 NDI discovery server", ct);
            var data = Payload(verified.RootElement);
            var enabled = data.TryGetProperty("enable", out var enable) &&
                          (enable.ValueKind == JsonValueKind.True ||
                           (enable.ValueKind == JsonValueKind.Number && enable.TryGetInt32(out var number) && number != 0) ||
                           (enable.ValueKind == JsonValueKind.String && bool.TryParse(enable.GetString(), out var parsed) && parsed));
            var matches = data.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array &&
                          servers.EnumerateArray().Any(server =>
                              string.Equals(String(server, "ip"), ipAddress, StringComparison.Ordinal) &&
                              string.Equals(String(server, "group_name"), group, StringComparison.Ordinal));
            if (enabled && matches) return;
        }
        throw new DeviceApiException($"N6 did not retain NDI Discovery Server {ipAddress} for group '{group}' after role selection.");
    }

    private async Task<JsonDocument> PostWhenCodecReadyAsync(
        HttpClient client,
        string path,
        object body,
        string description,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);
        DeviceApiException? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                return await PostAsync(client, path, body, description, ct);
            }
            catch (DeviceApiException ex) when (IsModeServiceUnavailable(ex))
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new DeviceApiException(
            $"The N6 codec proxy remained unavailable (0201001) while attempting to {description}. Power-cycle the N6 and retry; if the error persists, reflash a known-good firmware or contact Kiloview support.",
            last);
    }

    private async Task ConfigureKiloLinkAsync(HttpClient client, OnboardingRequest settings, CancellationToken ct)
    {
        var body = new { ip = settings.KiloLinkServerIp, port = settings.KiloLinkPort, ifname = new[] { "eth0" }, key = settings.KiloLinkOnboardingCode, crypto = false, enable = true };
        DeviceApiException? last = null;
        foreach (var path in new[] { "/api/KiloLink/set", "/api/kilolink/set" })
        {
            try { using var _ = await PostAsync(client, path, body, "configure N6 KiloLink", ct); return; }
            catch (DeviceApiException ex) { last = ex; }
        }
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var _ = await PostAsync(client, "/kilolink/kilolink/Set.json",
                    new { cfg = body },
                    "configure N6 KiloLink",
                    ct);
                return;
            }
            catch (DeviceApiException ex) { last = ex; }

            try
            {
                if (await KiloLinkConfigurationMatchesAsync(client, settings, ct)) return;
            }
            catch (DeviceApiException ex) { last = ex; }

            if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
        }
        try
        {
            var legacy = $"/api/platform/set.json?MASTER_ADDR={Uri.EscapeDataString(settings.KiloLinkServerIp)}&MASTER_PORT={settings.KiloLinkPort}&AUTH_CODE={Uri.EscapeDataString(settings.KiloLinkOnboardingCode)}";
            using var _ = await GetAsync(client, legacy, "configure N6 KiloLink", ct);
            return;
        }
        catch (DeviceApiException ex) { last = ex; }
        try
        {
            if (await KiloLinkConfigurationMatchesAsync(client, settings, ct)) return;
        }
        catch (DeviceApiException ex) { last = ex; }
        throw new DeviceApiException("This N6 firmware did not expose the KiloLink configuration endpoint. Update its firmware or configure KiloLink once in the device UI.", last);
    }

    private async Task<bool> KiloLinkConfigurationMatchesAsync(HttpClient client, OnboardingRequest settings, CancellationToken ct)
    {
        using var current = await GetAsync(client, "/kilolink/kilolink/Get.json", "verify N6 KiloLink", ct);
        if (!current.RootElement.TryGetProperty("cfg", out var config) || config.ValueKind != JsonValueKind.Object)
            return false;
        var enabled = config.TryGetProperty("enable", out var enabledValue)
            && enabledValue.ValueKind is JsonValueKind.True or JsonValueKind.String
            && (enabledValue.ValueKind == JsonValueKind.True
                || string.Equals(enabledValue.GetString(), "true", StringComparison.OrdinalIgnoreCase));
        var interfaces = config.TryGetProperty("ifname", out var names) && names.ValueKind == JsonValueKind.Array
            ? names.EnumerateArray().Select(value => value.ToString()).ToArray()
            : [];
        var port = config.TryGetProperty("port", out var portValue)
            && (portValue.TryGetInt32(out var numericPort)
                || portValue.ValueKind == JsonValueKind.String && int.TryParse(portValue.GetString(), out numericPort))
                ? numericPort
                : 0;
        return enabled
            && string.Equals(String(config, "ip"), settings.KiloLinkServerIp, StringComparison.Ordinal)
            && port == settings.KiloLinkPort
            && string.Equals(String(config, "key"), settings.KiloLinkOnboardingCode, StringComparison.Ordinal)
            && interfaces.Contains("eth0", StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetRoleAsync(DeviceRole role, CancellationToken ct)
    {
        if (role == DeviceRole.Unknown) throw new ArgumentException("Role must be Encoder or Decoder.");
        var target = role == DeviceRole.Decoder ? "decoder" : "encoder";
        try
        {
            await SwitchModeAndWaitAsync(target, ct);
        }
        catch (DeviceApiException ex) when (IsModeServiceUnavailable(ex))
        {
            // A rejected switch is a codec-proxy fault, not evidence that the
            // opposite mode is active. Rebooting and retrying here previously
            // made a sick unit repeatedly restart during onboarding.
            throw new DeviceApiException(
                $"The N6 codec proxy service is unavailable (0201001), so the device was not switched to {target} mode. Power-cycle the N6 and retry after its web UI no longer reports a codec proxy error.",
                ex);
        }
    }

    public async Task<HdmiInputProbeResult> ProbeEncoderInputAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var status = await GetAsync(client, "/api/device/status.json?types=ndihx", "read N6 HDMI input", ct);
        var data = Payload(status.RootElement);
        var signal = FirstString(data, "signal", "video_signal", "input_signal");
        var resolution = FirstString(data, "resolution", "input_resolution", "video_resolution");
        var present = !string.IsNullOrWhiteSpace(signal)
            ? SignalIsPresent(signal)
            : SignalIsPresent(resolution);
        return new(present, present && !string.IsNullOrWhiteSpace(resolution) ? resolution : null);
    }

    public async Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var output = await GetAsync(client, "/api/decoder/output/get.json", "read N6 HDMI negotiation", ct);
        var resolution = String(output.RootElement.GetProperty("data"), "resolution");
        var connected = !string.IsNullOrWhiteSpace(resolution) && resolution is not "none" and not "0" and not "unknown";
        return new(connected, connected ? resolution : null);
    }

    private async Task SwitchModeAndWaitAsync(string target, CancellationToken ct)
    {
        using (var client = await AuthorizedAsync(ct))
        {
            string? currentMode = null;
            try
            {
                using var current = await GetAsync(client, "/api/mode/get.json", "read N6 mode before switch", ct);
                currentMode = String(Payload(current.RootElement), "mode");
                if (string.Equals(currentMode, target, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch (DeviceApiException ex) when (IsModeServiceUnavailable(ex))
            {
                // Do not reboot before trying the switch. Firmware 2.00 can
                // reject mode/get with 0201001 while mode/switch still works.
            }

            // N6 firmware 2.00 can wedge its codec proxy at 0201001 when an
            // active decoder preview is carried across a Decoder -> Encoder
            // transition. The device UI exposes those previews as disposable
            // presets, so clear them before requesting Encoder mode. Onboarding
            // repopulates the bank after final roles are known.
            if (string.Equals(currentMode, "decoder", StringComparison.OrdinalIgnoreCase) && target == "encoder")
                await ClearDecoderPreviewsAsync(client, ct);

            using var switched = await PostAsync(client, "/api/mode/switch.json", new { mode = target }, "switch N6 mode", ct);
        }

        var end = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        DeviceApiException? last = null;
        var consecutiveReadyReads = 0;
        while (DateTimeOffset.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            try
            {
                using var client = await AuthorizedAsync(ct);
                using var status = await GetAsync(client, "/api/mode/status.json", "verify N6 mode-change status", ct);
                using var mode = await GetAsync(client, "/api/mode/get.json", "verify N6 mode", ct);
                using var targetService = target == "decoder"
                    ? await GetAsync(client, "/api/decoderMode/current/get.json", "verify N6 decoder service", ct)
                    : await GetAsync(client, "/api/device/status.json?types=ndihx", "verify N6 encoder service", ct);

                var ready = string.Equals(String(Payload(status.RootElement), "status"), "ready", StringComparison.OrdinalIgnoreCase);
                var correctMode = string.Equals(String(Payload(mode.RootElement), "mode"), target, StringComparison.OrdinalIgnoreCase);
                consecutiveReadyReads = ready && correctMode ? consecutiveReadyReads + 1 : 0;
                // The mode and target codec endpoints can appear briefly before
                // firmware 2.00 has finished settling, then regress to 0201001.
                // Require ten seconds of consecutive healthy reads, matching
                // the device UI's mode/status contract, before continuing.
                if (consecutiveReadyReads >= 5) return;
            }
            catch (Exception ex) when (ex is DeviceApiException or HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                consecutiveReadyReads = 0;
                last = ex as DeviceApiException ?? new DeviceApiException($"N6 {target} service is not ready.", ex);
            }
        }
        throw new DeviceApiException($"N6 mode service did not become ready in {target} mode.", last);
    }

    private async Task ClearDecoderPreviewsAsync(HttpClient client, CancellationToken ct)
    {
        using var current = await GetAsync(client, "/api/preview/get", "read N6 previews before changing to encoder mode", ct);
        foreach (var preview in PreviewPositions(current.RootElement).ToArray())
        {
            var positionId = Integer(preview, "id");
            if (positionId <= 0) continue;
            using var removed = await PostAsync(client, "/api/preview/source/remove",
                new { pos_id = positionId },
                $"stop N6 decoder preview {positionId} before changing mode",
                ct);
        }

        using var verified = await GetAsync(client, "/api/preview/get", "verify N6 previews stopped before changing mode", ct);
        if (PreviewPositions(verified.RootElement).Any())
            throw new DeviceApiException("The N6 retained an active decoder preview, so switching to Encoder mode was cancelled to protect its codec service.");
    }

    private static bool IsModeServiceUnavailable(DeviceApiException ex) =>
        ex.Message.Contains("0201001", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("mode service did not", StringComparison.OrdinalIgnoreCase);

    private static string FirstString(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = String(element, property);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "";
    }

    private static bool SignalIsPresent(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().Replace("_", " ").Replace("-", " ");
        return normalized is not "0" &&
               !normalized.Equals("none", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("no signal", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("offline", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ShowIdentityAsync(TitleCardSource source, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var targets = await PostAsync(client, "/api/decoder/discovery/set_manual_targets.json",
                    new { ip = new[] { source.LocalAddress }, group_name = new[] { source.Group } }, "configure N6 identity-card discovery", ct);
                try
                {
                    if (await TryShowIdentityWithLayoutAsync(client, source, ct)) return;
                }
                catch (DeviceApiException)
                {
                    // Older N6 firmware uses the legacy single-output endpoint below.
                }

                using var discovery = await GetAsync(client, "/api/decoder/discovery/get.json", "find N6 identity card", ct);
                var data = discovery.RootElement.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows : default;
                if (data.ValueKind == JsonValueKind.Array)
                {
                    var match = data.EnumerateArray().FirstOrDefault(row => String(row, "name").Contains(source.Name, StringComparison.OrdinalIgnoreCase));
                    if (match.ValueKind == JsonValueKind.Object)
                    {
                        var name = String(match, "name", source.Name);
                        var url = String(match, "original_url", String(match, "url"));
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            using var selected = await PostAsync(client, "/api/decoder/current/set.json", new { name, url }, "show N6 identity card", ct);
                            if (await WaitForIdentitySelectionAsync(client, source, url, ct)) return;
                        }
                    }
                }
            }
            catch (DeviceApiException) when (attempt < 19) { /* Decoder services can lag behind the web UI after a mode change. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new DeviceApiException($"The N6 did not discover its NDI identity source '{source.Name}'.");
    }

    private async Task<bool> TryShowIdentityWithLayoutAsync(
        HttpClient client,
        TitleCardSource source,
        CancellationToken ct)
    {
        using var sources = await PostAsync(client, "/api/source/groups/list",
            new { is_need_stream = true, show_template = false },
            "find N6 identity card in layout sources",
            ct);
        var match = DecoderGroupStreams(sources.RootElement)
            .FirstOrDefault(row => String(row, "name").Contains(source.Name, StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind != JsonValueKind.Object) return false;

        var streamId = String(match, "id");
        var streamName = String(match, "name", source.Name);
        var streamUrl = String(match, "url");
        if (string.IsNullOrWhiteSpace(streamId) || string.IsNullOrWhiteSpace(streamUrl)) return false;

        using var outputs = await GetAsync(client, "/api/output/list", "read N6 identity-card outputs", ct);
        var output = outputs.RootElement.TryGetProperty("data", out var outputRows)
            && outputRows.ValueKind == JsonValueKind.Array
            ? outputRows.EnumerateArray().FirstOrDefault(row => row.ValueKind == JsonValueKind.Object)
            : default;
        var outputId = String(output, "id");
        if (string.IsNullOrWhiteSpace(outputId)) return false;

        var outputPath = $"/api/output/get?output_id={Uri.EscapeDataString(outputId)}";
        using var current = await GetAsync(client, outputPath, "read N6 identity-card output layout", ct);
        var position = OutputPositions(current.RootElement).FirstOrDefault();
        var positionId = Integer(position, "id");
        if (positionId <= 0) return false;

        using var selected = await PostAsync(client, "/api/output/source/set", new
        {
            from = new { type = "source", output_id = outputId },
            to = new
            {
                type = "output",
                stream_id = streamId,
                stream_name = streamName,
                stream_url = streamUrl,
                output_id = outputId,
                pos_id = positionId
            }
        }, "show N6 identity card in output layout", ct);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            using var verified = await GetAsync(client, outputPath, "verify N6 identity-card output layout", ct);
            if (OutputPositions(verified.RootElement).Any(row =>
                    string.Equals(String(row, "stream_id"), streamId, StringComparison.OrdinalIgnoreCase)
                    || String(row, "stream_name").Contains(source.Name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static IEnumerable<JsonElement> OutputPositions(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("position", out var positions) || positions.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var position in positions.EnumerateArray())
            if (position.ValueKind == JsonValueKind.Object) yield return position;
    }

    private async Task<bool> WaitForIdentitySelectionAsync(HttpClient client, TitleCardSource source, string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            foreach (var path in new[] { "/api/decoder/current/get.json", "/api/decoderMode/current/get.json" })
            {
                try
                {
                    using var current = await GetAsync(client, path, "verify N6 identity card", ct);
                    var data = current.RootElement.TryGetProperty("data", out var value) ? value : default;
                    if (data.ValueKind != JsonValueKind.Object) continue;
                    var currentName = String(data, "name");
                    var currentUrl = String(data, "original_url", String(data, "url", String(data, "ip")));
                    if (currentName.Contains(source.Name, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(currentUrl) && string.Equals(currentUrl, url, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch (DeviceApiException) { }
            }
        }
        return false;
    }

    public async Task ConfigureDecoderFeedsAsync(IReadOnlyList<ManagedDevice> encoders, CancellationToken ct)
    {
        var ordered = encoders
            .OrderBy(encoder => encoder.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(encoder => encoder.IpAddress, StringComparer.Ordinal)
            .ToArray();
        const int previewCapacity = 10;
        if (ordered.Length > previewCapacity)
            throw new DeviceApiException($"The N6 has {previewCapacity} feed preset slots but this job contains {ordered.Length} encoders.");

        using var client = await AuthorizedAsync(ct, TimeSpan.FromSeconds(45));
        using var current = await GetAsync(client, "/api/preview/get", "read N6 decoder feed presets", ct);
        var existing = PreviewPositions(current.RootElement).ToArray();
        foreach (var preset in existing)
        {
            var positionId = Integer(preset, "id");
            if (positionId <= 0) continue;
            using var removed = await PostAsync(client, "/api/preview/source/remove",
                new { pos_id = positionId },
                $"clear N6 decoder feed preset {positionId}",
                ct);
        }
        if (ordered.Length == 0) return;

        var sources = new Dictionary<string, (string GroupId, JsonElement Stream)>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 30 && sources.Count < ordered.Length; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            using var discovery = await PostAsync(client, "/api/source/groups/list",
                new { is_need_stream = true, show_template = false },
                "discover encoder feeds for N6 presets",
                ct);
            var rows = DecoderGroupStreamsWithGroup(discovery.RootElement).ToArray();
            foreach (var encoder in ordered.Where(encoder => !sources.ContainsKey(encoder.Id)))
            {
                var match = rows
                    .Where(row => N6DecoderSourceScore(row.Stream, encoder) > 0)
                    .OrderByDescending(row => N6DecoderSourceScore(row.Stream, encoder))
                    // An N60 HB-only restart can leave its previous listener in
                    // Discovery Server results until that advertisement expires.
                    // The replacement listener is allocated after the old one;
                    // prefer it when identity and address scores are otherwise
                    // identical so presets do not retain the retired HX/HB URL.
                    .ThenByDescending(row => DiscoveredSourcePort(row.Stream))
                    .FirstOrDefault();
                if (match.Stream.ValueKind == JsonValueKind.Object)
                    sources[encoder.Id] = (match.GroupId, match.Stream.Clone());
            }
        }
        if (sources.Count != ordered.Length)
        {
            var missing = ordered.Where(encoder => !sources.ContainsKey(encoder.Id)).Select(encoder => encoder.Hostname);
            throw new DeviceApiException($"The N6 could not discover encoder feed(s): {string.Join(", ", missing)}. Confirm each NDI sender is running and visible to the selected Discovery Server.");
        }

        foreach (var encoder in ordered)
        {
            var sourceRecord = sources[encoder.Id];
            var source = sourceRecord.Stream;
            if (encoder.MulticastConfigured)
                await SetDecoderSourceTransportAsync(client, sourceRecord.GroupId, source, "multicast", ct);
            var streamId = String(source, "id");
            var streamName = String(source, "name", $"{encoder.Hostname} ({encoder.NdiChannelName})");
            var streamUrl = String(source, "url");
            using var added = await PostAsync(client, "/api/preview/source/modify", new
            {
                from = new { type = "source", stream_id = streamId, stream_name = streamName, stream_url = streamUrl, pos_id = "" },
                // Omitting pos_id appends a new preview preset. Supplying a
                // guessed slot identifier makes N6 firmware return 0304002.
                to = new { type = "preview", stream_id = streamId, stream_name = streamName, stream_url = streamUrl }
            }, $"add {encoder.Hostname} to an N6 decoder feed preset", ct);
        }

        using var verified = await GetAsync(client, "/api/preview/get", "verify N6 decoder feed presets", ct);
        var presets = PreviewPositions(verified.RootElement).ToArray();
        foreach (var encoder in ordered)
        {
            if (!presets.Any(preset => N6PreviewScore(preset, encoder) > 0))
                throw new DeviceApiException($"The N6 feed preset bank did not retain encoder {encoder.Hostname}.");
        }
    }

    private static IEnumerable<JsonElement> PreviewPositions(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("position", out var positions) || positions.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var position in positions.EnumerateArray())
            if (position.ValueKind == JsonValueKind.Object) yield return position;
    }

    private static int Integer(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static IEnumerable<JsonElement> DecoderGroupStreams(JsonElement root)
        => DecoderGroupStreamsWithGroup(root).Select(row => row.Stream);

    private static IEnumerable<(string GroupId, JsonElement Stream)> DecoderGroupStreamsWithGroup(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var groups) || groups.ValueKind != JsonValueKind.Array) yield break;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object ||
                !group.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) continue;
            var groupId = String(group, "id");
            foreach (var stream in streams.EnumerateArray())
                if (stream.ValueKind == JsonValueKind.Object) yield return (groupId, stream);
        }
    }

    private async Task SetDecoderSourceTransportAsync(
        HttpClient client,
        string groupId,
        JsonElement source,
        string transport,
        CancellationToken ct)
    {
        var streamId = String(source, "id");
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(streamId))
            throw new DeviceApiException("The N6 decoder source is missing its group or stream identifier.");
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(source.GetRawText())
            ?? throw new DeviceApiException("The N6 decoder source could not be prepared for multicast configuration.");
        payload["group_id"] = groupId;
        payload["stream_id"] = streamId;
        payload["ndi_name"] = String(source, "name");
        payload["trans_mode"] = transport;
        using var changed = await PostAsync(
            client,
            "/api/source/groups/streams/modify",
            payload,
            $"set N6 decoder source {streamId} to {transport}",
            ct);

        using var verified = await PostAsync(client, "/api/source/groups/list",
            new { is_need_stream = true, show_template = false },
            "verify N6 decoder source transport",
            ct);
        var retained = DecoderGroupStreamsWithGroup(verified.RootElement).FirstOrDefault(row =>
            string.Equals(row.GroupId, groupId, StringComparison.Ordinal)
            && string.Equals(String(row.Stream, "id"), streamId, StringComparison.Ordinal));
        if (retained.Stream.ValueKind != JsonValueKind.Object
            || !string.Equals(String(retained.Stream, "trans_mode"), transport, StringComparison.OrdinalIgnoreCase))
            throw new DeviceApiException($"The N6 decoder source {String(source, "name")} did not retain {transport} receive mode.");
    }

    private static int N6DecoderSourceScore(JsonElement source, ManagedDevice encoder)
    {
        var addressMatch = string.Equals(String(source, "address"), encoder.IpAddress, StringComparison.Ordinal);
        var name = String(source, "name", String(source, "ndi_name"));
        var channelMatch = !string.IsNullOrWhiteSpace(encoder.NdiChannelName) &&
            name.Contains(encoder.NdiChannelName, StringComparison.OrdinalIgnoreCase);
        var hostMatch = !string.IsNullOrWhiteSpace(encoder.Hostname) &&
            name.Contains(encoder.Hostname, StringComparison.OrdinalIgnoreCase);
        if (!addressMatch && !channelMatch && !hostMatch) return 0;
        return (channelMatch ? 8 : 0) + (hostMatch ? 4 : 0) + (addressMatch ? 2 : 0);
    }

    private static int DiscoveredSourcePort(JsonElement source)
    {
        var port = Integer(source, "listener_port");
        if (port > 0) return port;
        var url = String(source, "url");
        return Uri.TryCreate($"tcp://{url}", UriKind.Absolute, out var parsed) ? parsed.Port : 0;
    }

    private static int N6PreviewScore(JsonElement preset, ManagedDevice encoder)
    {
        var name = String(preset, "stream_name");
        var url = String(preset, "stream_url");
        var channelMatch = !string.IsNullOrWhiteSpace(encoder.NdiChannelName) &&
            name.Contains(encoder.NdiChannelName, StringComparison.OrdinalIgnoreCase);
        var hostMatch = !string.IsNullOrWhiteSpace(encoder.Hostname) &&
            name.Contains(encoder.Hostname, StringComparison.OrdinalIgnoreCase);
        var addressMatch = !string.IsNullOrWhiteSpace(encoder.IpAddress) &&
            url.StartsWith($"{encoder.IpAddress}:", StringComparison.Ordinal);
        return (channelMatch ? 8 : 0) + (hostMatch ? 4 : 0) + (addressMatch ? 2 : 0);
    }

    public async Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        foreach (var type in new[] { "ndihx", "ndifull" })
        {
            try { using var stream = await PostAsync(client, "/api/encoder/ndi/set_config.json", new { types = type, device_group = group, channel_name = channelName }, $"set N6 {type} name", ct); }
            catch (DeviceApiException) when (type == "ndifull") { /* Full NDI can be disabled on some firmware. */ }
        }
    }

    public async Task SetHostnameAsync(string hostname, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var changed = await PostAsync(client, "/api/device/set_hostname.json", new { hostname }, "set N6 hostname", ct);
    }

    public async Task ConfigureMulticastAsync(MulticastDeviceConfiguration settings, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var mode = await GetAsync(client, "/api/mode/get.json", "read N6 mode for multicast setup", ct);
        var role = String(mode.RootElement.GetProperty("data"), "mode");
        if (string.Equals(role, "encoder", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.NetPrefix is null || settings.Netmask is null)
                throw new ArgumentException("An N6 encoder requires a multicast prefix and subnet mask.");
            foreach (var type in new[] { "ndihx", "ndifull" })
            {
                try
                {
                    var retained = false;
                    DeviceApiException? configurationError = null;
                    for (var attempt = 0; attempt < 6 && !retained; attempt++)
                    {
                        if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        try
                        {
                            // Changing these parameters restarts the N6 sender.
                            // Always write the complete transport tuple so a
                            // previous Auto/unicast selection cannot survive.
                            using var configured = await PostAsync(client, "/api/encoder/ndi/set_config.json", new
                            {
                                types = type,
                                ndi_connection = "multicast",
                                netprefix = settings.NetPrefix,
                                netmask = settings.Netmask,
                                ttl = settings.Ttl
                            }, $"configure N6 {type} multicast sender", ct);
                            using var verified = await PostAsync(client, "/api/encoder/ndi/get_config.json", new { types = type }, $"verify N6 {type} multicast sender", ct);
                            var data = verified.RootElement.GetProperty("data");
                            retained = string.Equals(String(data, "ndi_connection"), "multicast", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(String(data, "netprefix"), settings.NetPrefix, StringComparison.Ordinal)
                                && string.Equals(String(data, "netmask"), settings.Netmask, StringComparison.Ordinal)
                                && (!data.TryGetProperty("ttl", out _) || Integer(data, "ttl") == settings.Ttl);
                        }
                        catch (DeviceApiException ex) when (attempt < 5)
                        {
                            configurationError = ex;
                        }
                    }
                    if (!retained)
                        throw configurationError ?? new DeviceApiException($"N6 {type} did not retain its multicast allocation.");
                }
                catch (DeviceApiException) when (type == "ndifull")
                {
                    // Full NDI is optional on some N6 firmware/licence combinations.
                }
            }
            return;
        }

        using var targets = await PostAsync(client, "/api/decoder/discovery/set_manual_targets.json", new
        {
            ip = settings.SenderAddresses,
            group_name = new[] { settings.Group }
        }, "configure N6 multicast source discovery", ct);
        await SetKnownDecoderSourceTransportsAsync(client, settings.SenderAddresses, "multicast", ct);
    }

    public async Task DisableMulticastAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var mode = await GetAsync(client, "/api/mode/get.json", "read N6 mode for unicast setup", ct);
        var role = String(mode.RootElement.GetProperty("data"), "mode");
        if (!string.Equals(role, "encoder", StringComparison.OrdinalIgnoreCase))
        {
            await SetKnownDecoderSourceTransportsAsync(client, null, "unicast", ct, multicastOnly: true);
            return;
        }

        foreach (var type in new[] { "ndihx", "ndifull" })
        {
            try
            {
                using var configured = await PostAsync(client, "/api/encoder/ndi/set_config.json", new
                {
                    types = type,
                    ndi_connection = "unicast"
                }, $"configure N6 {type} unicast sender", ct);
                using var verified = await PostAsync(client, "/api/encoder/ndi/get_config.json", new { types = type }, $"verify N6 {type} unicast sender", ct);
                var data = verified.RootElement.GetProperty("data");
                if (!string.Equals(String(data, "ndi_connection"), "unicast", StringComparison.OrdinalIgnoreCase))
                    throw new DeviceApiException($"N6 {type} did not confirm unicast mode.");
            }
            catch (DeviceApiException) when (type == "ndifull")
            {
                // Full NDI is optional on some N6 firmware/licence combinations.
            }
        }
    }

    private async Task SetKnownDecoderSourceTransportsAsync(
        HttpClient client,
        IReadOnlyList<string>? senderAddresses,
        string transport,
        CancellationToken ct,
        bool multicastOnly = false)
    {
        using var discovered = await PostAsync(client, "/api/source/groups/list",
            new { is_need_stream = true, show_template = false },
            $"discover N6 sources for {transport} receive mode",
            ct);
        var addresses = senderAddresses?.ToHashSet(StringComparer.Ordinal) ?? [];
        var sources = DecoderGroupStreamsWithGroup(discovered.RootElement)
            .Where(row => string.Equals(String(row.Stream, "type"), "ndi", StringComparison.OrdinalIgnoreCase))
            .Where(row => senderAddresses is null || addresses.Contains(String(row.Stream, "address")))
            .Where(row => !multicastOnly
                || string.Equals(String(row.Stream, "trans_mode"), "multicast", StringComparison.OrdinalIgnoreCase))
            .Select(row => (row.GroupId, Stream: row.Stream.Clone()))
            .ToArray();
        foreach (var source in sources)
            await SetDecoderSourceTransportAsync(client, source.GroupId, source.Stream, transport, ct);
    }

    public async Task BlankAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var color = await PostAsync(client, "/api/decoder/preset/set_blank.json", new { color = "#000000" }, "set N6 blank colour", ct);
        using var current = await PostAsync(client, "/api/decoder/current/set.json", new { id = "0" }, "blank N6 output", ct);
    }
}
