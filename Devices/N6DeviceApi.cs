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

    private async Task<HttpClient> AuthorizedAsync(CancellationToken ct)
    {
        var client = NewClient(TimeSpan.FromSeconds(8));
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
        using var mode = await GetAsync(client, "/api/mode/get.json", "read N6 mode", ct);
        var net = network.RootElement.GetProperty("data")[0];
        var ver = version.RootElement.GetProperty("data");
        var hostnameText = String(hostname.RootElement.GetProperty("data"), "hostname", "N6");
        var serialFromHostname = hostnameText.StartsWith("N6-", StringComparison.OrdinalIgnoreCase)
            ? hostnameText[3..]
            : "";
        var serial = String(ver, "serialNumber", String(ver, "serial_number", serialFromHostname));
        var mac = String(net, "mac", serial);
        if (string.IsNullOrWhiteSpace(serial)) serial = mac;
        var modeName = String(mode.RootElement.GetProperty("data"), "mode");
        return new ManagedDevice
        {
            Id = string.IsNullOrWhiteSpace(serial) ? mac : serial,
            IpAddress = String(net, "ip", IpAddress),
            MacAddress = mac,
            Hostname = hostnameText,
            Model = "N6",
            Family = DeviceFamily.N6,
            FirmwareVersion = String(ver, "softwareVersion"),
            IsStatic = String(net, "dynamic") == "n",
            Role = modeName == "decoder" ? DeviceRole.Decoder : DeviceRole.Encoder,
            Health = DeviceHealth.Online,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Credentials = Credentials
        };
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
        using var client = await AuthorizedAsync(ct);
        client.Timeout = TimeSpan.FromMinutes(20);
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
        using var discovery = await PostAsync(client, "/api/device/set_discovery_server.json",
            new { enable = true, servers = new[] { new { ip = settings.NdiDiscoveryServerIp, group_name = settings.JobName } } },
            "set N6 NDI discovery server",
            ct);
        foreach (var type in new[] { "ndihx", "ndifull" })
        {
            using var stream = await PostAsync(client, "/api/device/modify.json",
                new { types = type, device_group = settings.JobName, channel_name = channelName, machine_name = hostname },
                $"set N6 {type} identity",
                ct);
        }
        using var host = await PostAsync(client, "/api/device/set_hostname.json", new { hostname }, "set N6 hostname", ct);
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
            var legacy = $"/api/platform/set.json?MASTER_ADDR={Uri.EscapeDataString(settings.KiloLinkServerIp)}&MASTER_PORT={settings.KiloLinkPort}&AUTH_CODE={Uri.EscapeDataString(settings.KiloLinkOnboardingCode)}";
            using var _ = await GetAsync(client, legacy, "configure N6 KiloLink", ct);
            return;
        }
        catch (DeviceApiException ex) { last = ex; }
        throw new DeviceApiException("This N6 firmware did not expose the KiloLink configuration endpoint. Update its firmware or configure KiloLink once in the device UI.", last);
    }

    public async Task SetRoleAsync(DeviceRole role, CancellationToken ct)
    {
        if (role == DeviceRole.Unknown) throw new ArgumentException("Role must be Encoder or Decoder.");
        using var client = await AuthorizedAsync(ct);
        using var _ = await PostAsync(client, "/api/mode/switch.json", new { mode = role == DeviceRole.Decoder ? "decoder" : "encoder" }, "switch N6 mode", ct);
    }

    public async Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var output = await GetAsync(client, "/api/decoder/output/get.json", "read N6 HDMI negotiation", ct);
        var resolution = String(output.RootElement.GetProperty("data"), "resolution");
        var connected = !string.IsNullOrWhiteSpace(resolution) && resolution is not "none" and not "0" and not "unknown";
        return new(connected, connected ? resolution : null);
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
                            return;
                        }
                    }
                }
            }
            catch (DeviceApiException) when (attempt < 19) { /* Decoder services can lag behind the web UI after a mode change. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new DeviceApiException($"The N6 did not discover its NDI identity source '{source.Name}'.");
    }

    public async Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var host = await PostAsync(client, "/api/device/set_hostname.json", new { hostname }, "set N6 hostname", ct);
        foreach (var type in new[] { "ndihx", "ndifull" })
        {
            try { using var stream = await PostAsync(client, "/api/encoder/ndi/set_config.json", new { types = type, device_group = group, channel_name = channelName }, $"set N6 {type} name", ct); }
            catch (DeviceApiException) when (type == "ndifull") { /* Full NDI can be disabled on some firmware. */ }
        }
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
                    if (!string.Equals(String(data, "ndi_connection"), "multicast", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(String(data, "netprefix"), settings.NetPrefix, StringComparison.Ordinal)
                        || !string.Equals(String(data, "netmask"), settings.Netmask, StringComparison.Ordinal))
                        throw new DeviceApiException($"N6 {type} did not retain its multicast allocation.");
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
        // N6 exposes no separate receiver-transport endpoint. Its receiver
        // negotiates the sender-advertised multicast transport for every source
        // listed above.
    }

    public async Task DisableMulticastAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var mode = await GetAsync(client, "/api/mode/get.json", "read N6 mode for unicast setup", ct);
        var role = String(mode.RootElement.GetProperty("data"), "mode");
        if (!string.Equals(role, "encoder", StringComparison.OrdinalIgnoreCase))
        {
            // N6 decoders negotiate the transport advertised by each sender and
            // do not expose a separate receiver transport switch.
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

    public async Task BlankAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var color = await PostAsync(client, "/api/decoder/preset/set_blank.json", new { color = "#000000" }, "set N6 blank colour", ct);
        using var current = await PostAsync(client, "/api/decoder/current/set.json", new { id = "0" }, "blank N6 output", ct);
    }
}
