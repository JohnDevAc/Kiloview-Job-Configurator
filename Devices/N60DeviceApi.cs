using System.Text.Json;
using System.Net.Http.Headers;
using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

internal sealed class N60DeviceApi(
    string ipAddress,
    DeviceCredentials credentials,
    IHttpClientFactory clients) : HttpDeviceApi(ipAddress, credentials, clients), IDeviceApi
{
    private async Task<HttpClient> AuthorizedAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        var client = NewClient(timeout ?? TimeSpan.FromSeconds(8));
        client.DefaultRequestHeaders.TryAddWithoutValidation("App", "{\"language\":\"en\"}");
        using var login = await PostAsync(client, "/api/systemctrl/users/login", new { username = Credentials.Username, password = Credentials.Password }, "N60 login", ct);
        var data = login.RootElement.GetProperty("data");
        var token = String(data, "token");
        var alias = String(data, "alias", "Admin");
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
        var uri = new Uri($"http://{IpAddress}");
        Cookies.Add(uri, new System.Net.Cookie("language", "en"));
        Cookies.Add(uri, new System.Net.Cookie("user", Credentials.Username));
        Cookies.Add(uri, new System.Net.Cookie("alias", alias));
        Cookies.Add(uri, new System.Net.Cookie("token", token));
        ApplyCookies(client);
        return client;
    }

    public async Task<ManagedDevice> ReadAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var system = await GetAsync(client, "/api/systemctrl/system/getSystemInfo?version=true", "read N60 version", ct);
        using var hostname = await GetAsync(client, "/api/systemctrl/system/getHostname", "read N60 hostname", ct);
        using var network = await GetAsync(client, "/api/networkmanager/network/GetLinkinfo", "read N60 network", ct);
        var modeText = (await client.GetStringAsync("/api/codec/mode/get", ct)).Trim().Trim('"');
        var version = system.RootElement.GetProperty("data").GetProperty("version");
        var active = network.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(e => String(e, "status") == "up");
        if (active.ValueKind == JsonValueKind.Undefined) active = network.RootElement.GetProperty("data")[0];
        var serial = String(version, "serialNumber", String(active, "mac", IpAddress));
        return new ManagedDevice
        {
            Id = serial,
            IpAddress = String(active, "address", IpAddress),
            MacAddress = String(active, "mac", serial),
            Hostname = String(hostname.RootElement.GetProperty("data"), "hostname", "N60"),
            Model = "N60",
            Family = DeviceFamily.N60,
            FirmwareVersion = String(version, "softwareVersion"),
            IsStatic = String(active, "method") == "static",
            Role = modeText == "decode" ? DeviceRole.Decoder : DeviceRole.Encoder,
            Health = DeviceHealth.Online,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Credentials = Credentials
        };
    }

    public async Task ProvisionAccessAsync(DeviceCredentials targetCredentials, CancellationToken ct)
    {
        using var original = await AuthorizedAsync(ct);
        _ = await TryAcceptLicenseAsync(original, ct);
        if (!string.Equals(Credentials.Username, targetCredentials.Username, StringComparison.Ordinal) ||
            !string.Equals(Credentials.Password, targetCredentials.Password, StringComparison.Ordinal))
        {
            DeviceApiException? last = null;
            var body = new
            {
                username = targetCredentials.Username,
                id = targetCredentials.Username,
                alias = "Admin",
                password = targetCredentials.Password,
                confirmNewPassword = targetCredentials.Password,
                enable_web = true,
                enable_api = true
            };
            foreach (var path in new[] { "/api/systemctrl/users/modify", "/api/systemctrl/users/changePassword", "/api/systemctrl/users/initPassword" })
            {
                try { using var changed = await PostAsync(original, path, body, "set N60 onboarding credentials", ct); last = null; break; }
                catch (DeviceApiException ex) { last = ex; }
            }
            if (last is not null) throw new DeviceApiException("The N60 did not accept the required initial password change.", last);
        }

        var replacement = new N60DeviceApi(IpAddress, targetCredentials, Clients);
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
        form.Add(content, "file", package.FileName);
        form.Add(new StringContent(package.FileName), "path");
        ApplyCookies(client);
        using var response = await client.PostAsync("/api/systemctrl/system/upload", form, ct);
        using var accepted = await ReadJsonAsync(response, "upload N60 firmware", ct);
    }

    private async Task<bool> TryAcceptLicenseAsync(HttpClient client, CancellationToken ct)
    {
        var known = new[]
        {
            "/api/users/accept_eula.json",
            "/api/systemctrl/system/acceptEula",
            "/api/systemctrl/system/setEula",
            "/api/systemctrl/eula/accept",
            "/api/systemctrl/license/accept",
            "/api/systemctrl/system/acceptAgreement",
            "/api/systemctrl/system/setAgreement"
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
        using var network = await GetAsync(client, "/api/networkmanager/network/GetLinkinfo", "read N60 network", ct);
        var active = network.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(e => String(e, "status") == "up");
        if (active.ValueKind == JsonValueKind.Undefined) active = network.RootElement.GetProperty("data")[0];
        var ifname = String(active, "device", "eth0");
        try
        {
            using var _ = await PostAsync(client, "/api/networkmanager/network/SetEthernets",
                new { ifname, address, netmask = mask, gw = gateway, mac = String(active, "mac"), method = "static", dns },
                "set N60 static address", ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* Expected while the address changes. */ }
        catch (HttpRequestException) { /* The caller verifies the device at its target address. */ }
    }

    public async Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var networks = await GetAsync(client, "/api/kilolink/networks", "read N60 KiloLink interfaces", ct);
        var interfaces = networks.RootElement.TryGetProperty("list", out var list)
            ? list.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
            : new[] { "eth0" };
        using var kilo = await PostAsync(client, "/api/kilolink/set", new { ip = settings.KiloLinkServerIp, port = settings.KiloLinkPort, ifname = interfaces, key = settings.KiloLinkOnboardingCode, crypto = false, enable = true }, "configure N60 KiloLink", ct);
        await SetIdentityAndDiscoveryAsync(client, hostname, channelName, settings.JobName, settings.NdiDiscoveryServerIp, ct);
    }

    private async Task SetIdentityAndDiscoveryAsync(HttpClient client, string hostname, string channel, string group, string? discoveryIp, CancellationToken ct)
    {
        using var host = await GetAsync(client, $"/api/systemctrl/system/setHostname?name={Uri.EscapeDataString(hostname)}", "set N60 hostname", ct);
        if (!string.IsNullOrWhiteSpace(discoveryIp))
        {
            using var discovery = await PostAsync(client, "/api/codec/discovery/setDiscoveryServer",
                new { enable = true, servers = new[] { new { ip = discoveryIp, group_name = group } } },
                "set N60 NDI discovery server",
                ct);
        }
        foreach (var stream in new[] { ("main", "ndi-hx"), ("main_full", "ndi-full") })
        {
            try
            {
                object body = new { group, channel_name = channel };
                using var configured = await PostAsync(client, $"/api/codec/streams/{stream.Item1}/{stream.Item2}/set", body, $"configure N60 {stream.Item2}", ct);
            }
            catch (DeviceApiException) when (stream.Item1 == "main_full") { /* Full NDI can be disabled. */ }
        }
    }

    public async Task SetRoleAsync(DeviceRole role, CancellationToken ct)
    {
        if (role == DeviceRole.Unknown) throw new ArgumentException("Role must be Encoder or Decoder.");
        using var client = await AuthorizedAsync(ct);
        var response = await client.PostAsync($"/api/codec/mode/set?mode={(role == DeviceRole.Decoder ? "decode" : "encode")}", null, ct);
        if (!response.IsSuccessStatusCode) throw new DeviceApiException($"Switch N60 mode failed with HTTP {(int)response.StatusCode}.");
    }

    public async Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var output = await GetAsync(client, "/api/codec/decode/get", "read N60 HDMI negotiation", ct);
        var resolution = String(output.RootElement.GetProperty("data"), "output_resolution");
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
                using var targets = await PostAsync(client, "/api/codec/discovery/addManualIpsGroups",
                    new { groups = new[] { source.Group }, manuals = new[] { source.LocalAddress } }, "configure N60 identity-card discovery", ct);
                using var discovery = await GetAsync(client, "/api/codec/discovery/scan", "find N60 identity card", ct);
                var data = discovery.RootElement.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows : default;
                if (data.ValueKind == JsonValueKind.Array)
                {
                    var match = data.EnumerateArray().FirstOrDefault(row => String(row, "name").Contains(source.Name, StringComparison.OrdinalIgnoreCase));
                    if (match.ValueKind == JsonValueKind.Object)
                    {
                        var name = String(match, "name", source.Name);
                        var url = String(match, "original_url", String(match, "url"));
                        var group = String(match, "group", String(match, "group_name", source.Group));
                        var id = match.TryGetProperty("id", out var index) && index.TryGetInt32(out var value) ? value : 0;
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            using var selected = await PostAsync(client, "/api/codec/decode/addSpec", new { id, name, url, group }, "show N60 identity card", ct);
                            return;
                        }
                    }
                }
            }
            catch (DeviceApiException) when (attempt < 19) { /* Decoder services can lag behind the web UI after a mode change. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new DeviceApiException($"The N60 did not discover its NDI identity source '{source.Name}'.");
    }

    public async Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        await SetIdentityAndDiscoveryAsync(client, hostname, channelName, group, null, ct);
    }

    public async Task ConfigureMulticastAsync(MulticastDeviceConfiguration settings, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        var mode = (await client.GetStringAsync("/api/codec/mode/get", ct)).Trim().Trim('"');
        if (string.Equals(mode, "encode", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.NetPrefix is null || settings.Netmask is null)
                throw new ArgumentException("An N60 encoder requires a multicast prefix and subnet mask.");
            foreach (var stream in new[] { ("main", "ndi-hx"), ("main_full", "ndi-full") })
            {
                try
                {
                    using var configured = await PostAsync(client, $"/api/codec/streams/{stream.Item1}/{stream.Item2}/set", new
                    {
                        connection = "multicast",
                        netprefix = settings.NetPrefix,
                        netmask = settings.Netmask,
                        ttl = settings.Ttl,
                        types = stream.Item2
                    }, $"configure N60 {stream.Item2} multicast sender", ct);
                    using var verified = await GetAsync(client, $"/api/codec/streams/{stream.Item1}/{stream.Item2}/get", $"verify N60 {stream.Item2} multicast sender", ct);
                    var data = verified.RootElement.TryGetProperty("data", out var wrapped)
                        ? wrapped
                        : verified.RootElement;
                    if (!string.Equals(String(data, "connection"), "multicast", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(String(data, "netprefix"), settings.NetPrefix, StringComparison.Ordinal)
                        || !string.Equals(String(data, "netmask"), settings.Netmask, StringComparison.Ordinal))
                        throw new DeviceApiException($"N60 {stream.Item2} did not retain its multicast allocation.");
                }
                catch (DeviceApiException) when (stream.Item1 == "main_full")
                {
                    // Full NDI can be disabled on some N60 configurations.
                }
            }
            return;
        }

        using var connection = await PostAsync(client, "/api/codec/decode/setConnection",
            new { ndi_connection = "multicast" },
            "set N60 decoder multicast receive mode",
            ct);
        using var targets = await PostAsync(client, "/api/codec/discovery/addManualIpsGroups", new
        {
            groups = new[] { settings.Group },
            manuals = settings.SenderAddresses
        }, "register every multicast sender on the N60 decoder", ct);
    }

    public async Task DisableMulticastAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        var mode = (await client.GetStringAsync("/api/codec/mode/get", ct)).Trim().Trim('"');
        if (!string.Equals(mode, "encode", StringComparison.OrdinalIgnoreCase))
        {
            using var connection = await PostAsync(client, "/api/codec/decode/setConnection",
                new { ndi_connection = "unicast" },
                "set N60 decoder unicast receive mode",
                ct);
            return;
        }

        foreach (var stream in new[] { ("main", "ndi-hx"), ("main_full", "ndi-full") })
        {
            try
            {
                using var configured = await PostAsync(client, $"/api/codec/streams/{stream.Item1}/{stream.Item2}/set", new
                {
                    connection = "unicast",
                    types = stream.Item2
                }, $"configure N60 {stream.Item2} unicast sender", ct);
                using var verified = await GetAsync(client, $"/api/codec/streams/{stream.Item1}/{stream.Item2}/get", $"verify N60 {stream.Item2} unicast sender", ct);
                var data = verified.RootElement.TryGetProperty("data", out var wrapped)
                    ? wrapped
                    : verified.RootElement;
                if (!string.Equals(String(data, "connection"), "unicast", StringComparison.OrdinalIgnoreCase))
                    throw new DeviceApiException($"N60 {stream.Item2} did not confirm unicast mode.");
            }
            catch (DeviceApiException) when (stream.Item1 == "main_full")
            {
                // Full NDI can be disabled on some N60 configurations.
            }
        }
    }

    public async Task BlankAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var color = await PostAsync(client, "/api/codec/preset/set_blank_color", new { BlankColor = "#000000" }, "set N60 blank colour", ct);
        using var blank = await PostAsync(client, "/api/codec/decode/add", new { id = 0 }, "blank N60 output", ct);
    }
}
