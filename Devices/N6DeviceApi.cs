using System.Text.Json;
using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

internal sealed class N6DeviceApi(string ipAddress, DeviceCredentials credentials) : HttpDeviceApi(ipAddress, credentials), IDeviceApi
{
    private async Task<HttpClient> AuthorizedAsync(CancellationToken ct)
    {
        var client = NewClient(TimeSpan.FromSeconds(8));
        using var login = await PostAsync(client, "/api/user/authorize.json", new { user = Credentials.Username, password = Credentials.Password }, "N6 login", ct);
        var data = login.RootElement.GetProperty("data");
        var token = String(data, "token");
        Cookies.Add(new Uri($"http://{IpAddress}"), new System.Net.Cookie("token", token));
        return client;
    }

    public async Task<ManagedDevice> ReadAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var version = await GetAsync(client, "/api/sys/version.json", "read N6 version", ct);
        using var hostname = await GetAsync(client, "/api/device/get_hostname.json", "read N6 hostname", ct);
        using var network = await GetAsync(client, "/api/network/get.json", "read N6 network", ct);
        using var mode = await GetAsync(client, "/api/mode/get.json", "read N6 mode", ct);
        var net = network.RootElement.GetProperty("data")[0];
        var ver = version.RootElement.GetProperty("data");
        var serial = String(ver, "serialNumber", String(ver, "serial_number", String(net, "mac", IpAddress)));
        var mac = String(net, "mac", serial);
        var modeName = String(mode.RootElement.GetProperty("data"), "mode");
        return new ManagedDevice
        {
            Id = string.IsNullOrWhiteSpace(serial) ? mac : serial,
            IpAddress = String(net, "ip", IpAddress),
            MacAddress = mac,
            Hostname = String(hostname.RootElement.GetProperty("data"), "hostname", "N6"),
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

    public async Task SetNetworkAsync(string address, string mask, string gateway, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var network = await GetAsync(client, "/api/network/get.json", "read N6 network", ct);
        var net = network.RootElement.GetProperty("data")[0];
        var device = String(net, "device", "eth0");
        using var _ = await PostAsync(client, "/api/network/set.json", new { device, dynamic = "n", ip = address, mask, gw = gateway, dns = "" }, "set N6 static address", ct);
    }

    public async Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        await ConfigureKiloLinkAsync(client, settings, ct);
        using var discovery = await PostAsync(client, "/api/device/set_discovery_server.json", new { address = settings.NdiDiscoveryServerIp, enable = true }, "set N6 NDI discovery server", ct);
        foreach (var type in new[] { "ndihx", "ndifull" })
        {
            using var stream = await PostAsync(client, "/api/encoder/ndi/set_config.json", new { types = type, device_group = settings.JobName, channel_name = channelName }, $"set N6 {type} identity", ct);
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

    public async Task BlankAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var color = await PostAsync(client, "/api/decoder/preset/set_blank.json", new { color = "#000000" }, "set N6 blank colour", ct);
        using var current = await PostAsync(client, "/api/decoder/current/set.json", new { id = "0" }, "blank N6 output", ct);
    }
}
