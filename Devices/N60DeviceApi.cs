using System.Text.Json;
using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

internal sealed class N60DeviceApi(string ipAddress, DeviceCredentials credentials) : HttpDeviceApi(ipAddress, credentials), IDeviceApi
{
    private async Task<HttpClient> AuthorizedAsync(CancellationToken ct)
    {
        var client = NewClient(TimeSpan.FromSeconds(8));
        client.DefaultRequestHeaders.TryAddWithoutValidation("App", "{\"language\":\"en\"}");
        using var login = await PostAsync(client, "/api/systemctrl/users/login", new { username = Credentials.Username, password = Credentials.Password }, "N60 login", ct);
        var data = login.RootElement.GetProperty("data");
        var token = String(data, "token");
        var alias = String(data, "alias", "Admin");
        var uri = new Uri($"http://{IpAddress}");
        Cookies.Add(uri, new System.Net.Cookie("language", "en"));
        Cookies.Add(uri, new System.Net.Cookie("user", Credentials.Username));
        Cookies.Add(uri, new System.Net.Cookie("alias", alias));
        Cookies.Add(uri, new System.Net.Cookie("token", token));
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

    public async Task SetNetworkAsync(string address, string mask, string gateway, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var network = await GetAsync(client, "/api/networkmanager/network/GetLinkinfo", "read N60 network", ct);
        var active = network.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(e => String(e, "status") == "up");
        if (active.ValueKind == JsonValueKind.Undefined) active = network.RootElement.GetProperty("data")[0];
        var ifname = String(active, "device", "eth0");
        using var _ = await PostAsync(client, "/api/networkmanager/network/SetEthernets",
            new { ifname, address, netmask = mask, gw = gateway, mac = String(active, "mac"), method = "static", dns = String(active, "dns") },
            "set N60 static address", ct);
    }

    public async Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var networks = await GetAsync(client, "/api/KiloLink/networks", "read N60 KiloLink interfaces", ct);
        var interfaces = networks.RootElement.TryGetProperty("list", out var list)
            ? list.EnumerateArray().Select(x => x.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
            : new[] { "eth0" };
        using var kilo = await PostAsync(client, "/api/KiloLink/set", new { ip = settings.KiloLinkServerIp, port = settings.KiloLinkPort, ifname = interfaces, key = settings.KiloLinkOnboardingCode, crypto = false, enable = true }, "configure N60 KiloLink", ct);
        await SetIdentityAndDiscoveryAsync(client, hostname, channelName, settings.JobName, settings.NdiDiscoveryServerIp, ct);
    }

    private async Task SetIdentityAndDiscoveryAsync(HttpClient client, string hostname, string channel, string group, string? discoveryIp, CancellationToken ct)
    {
        using var host = await GetAsync(client, $"/api/systemctrl/system/setHostname?name={Uri.EscapeDataString(hostname)}", "set N60 hostname", ct);
        foreach (var stream in new[] { ("main", "ndi-hx"), ("main_full", "ndi-full") })
        {
            try
            {
                object body = string.IsNullOrWhiteSpace(discoveryIp)
                    ? new { group, channel_name = channel, types = stream.Item2 }
                    : new { group, channel_name = channel, types = stream.Item2, discovery_server = new { enable = true, address = discoveryIp } };
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

    public async Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        await SetIdentityAndDiscoveryAsync(client, hostname, channelName, group, null, ct);
    }

    public async Task BlankAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var color = await PostAsync(client, "/api/codec/preset/set_blank_color", new { BlankColor = "#000000" }, "set N60 blank colour", ct);
        using var blank = await PostAsync(client, "/api/codec/decode/add", new { id = 0 }, "blank N60 output", ct);
    }
}
