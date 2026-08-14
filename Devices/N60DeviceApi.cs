using System.Text.Json;
using System.Net.Http.Headers;
using NDIJobConfigurator.Core;

namespace NDIJobConfigurator.Devices;

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
        await EnsureHbOnlyEncodingAsync(client, ct);
        await SetIdentityAndDiscoveryAsync(client, hostname, channelName, settings.JobName, settings.NdiDiscoveryServerIp, ct);
    }

    private async Task SetIdentityAndDiscoveryAsync(HttpClient client, string hostname, string channel, string group, string? discoveryIp, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            using var host = await GetAsync(client, $"/api/systemctrl/system/setHostname?name={Uri.EscapeDataString(hostname)}", "set N60 hostname", ct);
        }
        if (!string.IsNullOrWhiteSpace(discoveryIp))
        {
            using var discovery = await RetryRateLimitedAsync(
                () => PostAsync(client, "/api/codec/discovery/setDiscoveryServer",
                    new { enable = true, servers = new[] { new { ip = discoveryIp, group_name = group } } },
                    "set N60 NDI discovery server",
                    ct),
                ct);
        }
        const string stream = "main_full";
        const string type = "ndi-full";
        // Onboarding deliberately selects the dedicated NDI-HB mode. Do not
        // write identity settings to the inactive HX stream: doing so can wake
        // the shared HX/HB mode and advertise two senders with one identity.
        await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
        object body = new { group, group_server = group, channel_name = channel };
        using (var configured = await RetryRateLimitedAsync(
                   () => PostAsync(client, $"/api/codec/streams/{stream}/{type}/set", body, "configure N60 ndi-full", ct),
                   ct))
        {
        }
        await VerifyNdiGroupAsync(client, stream, type, group, ct);
        await RestartHbSenderAsync(client, ct);
    }

    private async Task EnsureHbOnlyEncodingAsync(HttpClient client, CancellationToken ct)
    {
        using var current = await RetryRateLimitedAsync(
            () => GetAsync(client, "/api/codec/encoders/get_encode", "read N60 encoder mode", ct),
            ct);
        var currentMode = String(Payload(current.RootElement), "encode_mode");
        if (!string.Equals(currentMode, "hb", StringComparison.OrdinalIgnoreCase))
        {
            using var changed = await RetryRateLimitedAsync(
                () => PostAsync(client, "/api/codec/encoders/choose_encode", new { encode_mode = "hb" }, "select N60 NDI-HB no-record mode", ct),
                ct);
        }

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            if (attempt > 1) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            try
            {
                using var selected = await RetryRateLimitedAsync(
                    () => GetAsync(client, "/api/codec/encoders/get_encode", "verify N60 encoder mode", ct),
                    ct);
                using var hb = await RetryRateLimitedAsync(
                    () => GetAsync(client, "/api/codec/streams/main_full/ndi-full/get", "verify N60 NDI-HB sender", ct),
                    ct);
                using var hx = await RetryRateLimitedAsync(
                    () => GetAsync(client, "/api/codec/streams/main/ndi-hx/get", "verify N60 NDI-HX sender is disabled", ct),
                    ct);
                var selectedMode = String(Payload(selected.RootElement), "encode_mode");
                var hbEnabled = Boolean(Payload(hb.RootElement), "enable");
                var hxEnabled = Boolean(Payload(hx.RootElement), "enable");
                if (string.Equals(selectedMode, "hb", StringComparison.OrdinalIgnoreCase) && hbEnabled && !hxEnabled)
                    return;
            }
            catch (DeviceApiException) when (attempt < 20)
            {
                // Codec endpoints can briefly disappear while the encoder mode
                // restarts. The final attempt remains actionable for the user.
            }
        }
        throw new DeviceApiException("N60 did not enter NDI-HB (no record) mode with NDI-HX disabled.");
    }

    private async Task RestartHbSenderAsync(HttpClient client, CancellationToken ct)
    {
        using var restarted = await RetryRateLimitedAsync(
            () => GetAsync(client, "/api/codec/streams/main_full/reset", "restart N60 NDI-HB sender", ct),
            ct);
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            try
            {
                using var current = await RetryRateLimitedAsync(
                    () => GetAsync(client, "/api/codec/streams/main_full/ndi-full/get", "verify restarted N60 NDI-HB sender", ct),
                    ct);
                if (Boolean(Payload(current.RootElement), "enable")) return;
            }
            catch (DeviceApiException) when (attempt < 12) { }
        }
        throw new DeviceApiException("N60 NDI-HB sender did not return after its configuration restart.");
    }

    private static bool Boolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return false;
        return value.ValueKind == JsonValueKind.True ||
               (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number != 0) ||
               (value.ValueKind == JsonValueKind.String &&
                (bool.TryParse(value.GetString(), out var boolean) ? boolean :
                 int.TryParse(value.GetString(), out var numeric) && numeric != 0));
    }

    public async Task ConfigureDiscoveryServerAsync(string ipAddress, string group, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var configured = await RetryRateLimitedAsync(
            () => PostAsync(client, "/api/codec/discovery/setDiscoveryServer",
                new { enable = true, servers = new[] { new { ip = ipAddress, group_name = group } } },
                "set N60 NDI discovery server after role selection",
                ct),
            ct);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
            using var verified = await RetryRateLimitedAsync(
                () => GetAsync(client, "/api/codec/discovery/getDiscoveryServer", "verify N60 NDI discovery server", ct),
                ct);
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
        throw new DeviceApiException($"N60 did not retain NDI Discovery Server {ipAddress} for group '{group}' after role selection.");
    }

    private async Task VerifyNdiGroupAsync(HttpClient client, string stream, string type, string expectedGroup, CancellationToken ct)
    {
        string actualGroup = "";
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
            using var verified = await RetryRateLimitedAsync(
                () => GetAsync(client, $"/api/codec/streams/{stream}/{type}/get", $"verify N60 {type} identity", ct),
                ct);
            actualGroup = String(Payload(verified.RootElement), "group");
            if (string.Equals(actualGroup, expectedGroup, StringComparison.Ordinal)) return;
        }
        throw new DeviceApiException($"N60 {type} retained NDI group '{actualGroup}' instead of '{expectedGroup}'.");
    }

    private static async Task<JsonDocument> RetryRateLimitedAsync(Func<Task<JsonDocument>> request, CancellationToken ct)
    {
        DeviceApiException? last = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try { return await request(); }
            catch (DeviceApiException ex) when (ex.Message.Contains("Request too often", StringComparison.OrdinalIgnoreCase))
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(750 * attempt), ct);
            }
        }
        throw last ?? new DeviceApiException("N60 request did not complete.");
    }

    public async Task SetRoleAsync(DeviceRole role, CancellationToken ct)
    {
        if (role == DeviceRole.Unknown) throw new ArgumentException("Role must be Encoder or Decoder.");
        using var client = await AuthorizedAsync(ct);
        var response = await client.PostAsync($"/api/codec/mode/set?mode={(role == DeviceRole.Decoder ? "decode" : "encode")}", null, ct);
        if (!response.IsSuccessStatusCode) throw new DeviceApiException($"Switch N60 mode failed with HTTP {(int)response.StatusCode}.");
    }

    public async Task<HdmiInputProbeResult> ProbeEncoderInputAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var capture = await PostAsync(client, "/api/codec/encoder/main/get_capture", new { }, "read N60 HDMI input", ct);
        // N60 2.45 returns capture fields at the JSON root; older builds wrap
        // the same object in `data`. Accept both shapes.
        var data = Payload(capture.RootElement);
        var signal = String(data, "signal");
        var present = SignalIsPresent(signal);
        var resolution = String(data, "resolution");
        if (string.IsNullOrWhiteSpace(resolution) &&
            data.TryGetProperty("width", out var width) && data.TryGetProperty("height", out var height))
        {
            var frameRate = String(data, "framerate");
            resolution = $"{width}x{height}{(string.IsNullOrWhiteSpace(frameRate) ? "" : $"p{frameRate}")}";
        }
        return new(present, present && !string.IsNullOrWhiteSpace(resolution) ? resolution : null);
    }

    public async Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var output = await GetAsync(client, "/api/codec/decode/get", "read N60 HDMI negotiation", ct);
        var resolution = String(output.RootElement.GetProperty("data"), "output_resolution");
        var connected = !string.IsNullOrWhiteSpace(resolution) && resolution is not "none" and not "0" and not "unknown";
        return new(connected, connected ? resolution : null);
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
                using var targets = await PostAsync(client, "/api/codec/discovery/addManualIpsGroups",
                    new { groups = new[] { source.Group }, manuals = new[] { source.LocalAddress } }, "configure N60 identity-card discovery", ct);
                using var discovery = await GetAsync(client, "/api/codec/discovery/scan", "find N60 identity card", ct);
                var data = discovery.RootElement.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows : default;
                if (data.ValueKind == JsonValueKind.Array)
                {
                    // N60 groups sources from the same sender machine beneath a
                    // top-level row. Each identity card uses its own NDI source,
                    // so later cards can appear only in a row's `children` array.
                    var match = FlattenDiscoveryRows(data)
                        .FirstOrDefault(row => String(row, "name").Contains(source.Name, StringComparison.OrdinalIgnoreCase));
                    if (match.ValueKind == JsonValueKind.Object)
                    {
                        var name = String(match, "name", source.Name);
                        var url = String(match, "original_url", String(match, "url"));
                        var group = String(match, "group", String(match, "group_name", source.Group));
                        var id = match.TryGetProperty("id", out var index) && index.TryGetInt32(out var value) ? value : 0;
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            var selection = new { id, name, url, group };
                            DeviceApiException? selectionError = null;
                            foreach (var path in new[] { "/api/codec/decode/add", "/api/codec/decode/addSpec" })
                            {
                                try
                                {
                                    using var selected = await PostAsync(client, path, selection, "show N60 identity card", ct);
                                    if (await WaitForIdentitySelectionAsync(client, source, url, ct))
                                    {
                                        // Source selection can restore the decoder's
                                        // stored forced output. Normalize only after
                                        // the identity source is confirmed active.
                                        await EnsureIdentityOutputAsync(client, ct);
                                        return;
                                    }
                                }
                                catch (DeviceApiException ex) { selectionError = ex; }
                            }
                            if (selectionError is not null) throw selectionError;
                        }
                    }
                }
            }
            catch (DeviceApiException) when (attempt < 19) { /* Decoder services can lag behind the web UI after a mode change. */ }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        throw new DeviceApiException($"The N60 did not discover its NDI identity source '{source.Name}'.");
    }

    private async Task EnsureIdentityOutputAsync(HttpClient client, CancellationToken ct)
    {
        using var current = await GetAsync(client, "/api/codec/decode/get", "read N60 identity-card output", ct);
        var data = Payload(current.RootElement);
        var outputMode = Number(data, "output_mode", 0);
        var outputChoice = String(data, "output_resolution_choose");
        var outputFrameRate = Number(data, "output_framerate", 0);
        if (outputMode == 0 &&
            string.Equals(outputChoice, "auto", StringComparison.OrdinalIgnoreCase) &&
            outputFrameRate == 0)
            return;

        // Identity cards are broadcast at 1080p59.94. A decoder left on a
        // forced output such as 2160p25/59.94 can receive the card correctly
        // while its attached display remains black. Auto makes HDMI follow the
        // broadcast-standard card format and preserves the device's audio,
        // HDCP, and colour-space choices.
        using var configured = await PostAsync(client, "/api/codec/decode/output_set", new
        {
            output_resolution = "auto",
            output_framerate = 0,
            hdmi_channels = Number(data, "hdmi_channels", 2),
            line_out_channels = Number(data, "line_out_channels", 2),
            hdcp = Number(data, "hdcp", 1),
            out_colorspace = Number(data, "out_colorspace", 0)
        }, "set N60 identity-card HDMI output to Auto", ct);
    }

    private static int Number(JsonElement element, string property, int fallback)
    {
        if (!element.TryGetProperty(property, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : fallback;
    }

    private static IEnumerable<JsonElement> FlattenDiscoveryRows(JsonElement rows)
    {
        if (rows.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            yield return row;
            if (!row.TryGetProperty("children", out var children)) continue;
            foreach (var child in FlattenDiscoveryRows(children)) yield return child;
        }
    }

    private async Task<bool> WaitForIdentitySelectionAsync(HttpClient client, TitleCardSource source, string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), ct);
            try
            {
                using var current = await GetAsync(client, "/api/codec/decode/get", "verify N60 identity card", ct);
                var data = current.RootElement.TryGetProperty("data", out var value) ? value : default;
                if (data.ValueKind == JsonValueKind.Object)
                {
                    var currentName = String(data, "name");
                    var currentUrl = String(data, "original_url", String(data, "url"));
                    if (currentName.Contains(source.Name, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(currentUrl) && string.Equals(currentUrl, url, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
            catch (DeviceApiException) when (attempt < 7) { }
        }
        return false;
    }

    public async Task ConfigureDecoderFeedsAsync(IReadOnlyList<ManagedDevice> encoders, CancellationToken ct)
    {
        var ordered = encoders
            .OrderBy(encoder => encoder.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(encoder => encoder.IpAddress, StringComparer.Ordinal)
            .ToArray();
        using var client = await AuthorizedAsync(ct, TimeSpan.FromSeconds(45));

        using var targets = await PostAsync(client, "/api/codec/discovery/addManualIpsGroups", new
        {
            groups = ordered.Select(encoder => encoder.NdiGroup)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            manuals = ordered.Select(encoder => encoder.IpAddress)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        }, "register encoder discovery targets on the N60 decoder", ct);

        using var current = await GetAsync(client, "/api/codec/preset/get", "read N60 decoder feed presets", ct);
        var slots = current.RootElement.GetProperty("data")
            .EnumerateArray()
            .Where(preset => Number(preset, "id", 0) > 0 && string.IsNullOrWhiteSpace(String(preset, "color")))
            .Select(preset => Number(preset, "id", 0))
            .Distinct()
            .Order()
            .ToArray();
        if (ordered.Length > slots.Length)
            throw new DeviceApiException($"The N60 has {slots.Length} feed preset slots but this job contains {ordered.Length} encoders.");

        var sources = ordered.Length == 0
            ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : await WaitForEncoderSourcesAsync(client, ordered, ct);

        // Replace the preset bank deterministically so feeds from an earlier job
        // cannot remain in unused positions. The colour/blank preset is excluded.
        foreach (var slot in slots)
            using (var removed = await PostAsync(client, "/api/codec/preset/remove", new { id = slot }, $"clear N60 decoder feed preset {slot}", ct)) { }

        for (var index = 0; index < ordered.Length; index++)
        {
            var encoder = ordered[index];
            var source = sources[encoder.Id];
            var url = String(source, "original_url", String(source, "url"));
            using var added = await PostAsync(client, "/api/codec/preset/add", new
            {
                position = slots[index],
                channel_name = String(source, "channel_name", encoder.NdiChannelName),
                device_name = String(source, "device_name", encoder.Hostname).Trim(),
                enable = 1,
                group = encoder.NdiGroup,
                ip = String(source, "ip", encoder.IpAddress),
                name = String(source, "name", $"{encoder.Hostname} ({encoder.NdiChannelName})"),
                port = Number(source, "port", 0),
                url,
                original_url = url,
                type = "ndi"
            }, $"add {encoder.Hostname} to N60 decoder feed preset {slots[index]}", ct);
        }

        using var verified = await GetAsync(client, "/api/codec/preset/get", "verify N60 decoder feed presets", ct);
        var presets = verified.RootElement.GetProperty("data").EnumerateArray().ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var encoder = ordered[index];
            var preset = presets.FirstOrDefault(candidate => Number(candidate, "id", 0) == slots[index]);
            if (preset.ValueKind != JsonValueKind.Object ||
                (!string.Equals(String(preset, "ip"), encoder.IpAddress, StringComparison.Ordinal) &&
                 !String(preset, "channel_name").Equals(encoder.NdiChannelName, StringComparison.OrdinalIgnoreCase)))
                throw new DeviceApiException($"N60 feed preset {slots[index]} did not retain encoder {encoder.Hostname}.");
        }
    }

    private async Task<Dictionary<string, JsonElement>> WaitForEncoderSourcesAsync(
        HttpClient client,
        IReadOnlyList<ManagedDevice> encoders,
        CancellationToken ct)
    {
        var found = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 30 && found.Count < encoders.Count; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
            using var discovery = await GetAsync(client, "/api/codec/discovery/scan", "discover encoder feeds for N60 presets", ct);
            var rows = discovery.RootElement.TryGetProperty("data", out var data)
                ? FlattenDiscoveryRows(data).ToArray()
                : [];
            foreach (var encoder in encoders.Where(encoder => !found.ContainsKey(encoder.Id)))
            {
                var match = rows
                    .Where(row => DecoderSourceScore(row, encoder) > 0)
                    .OrderByDescending(row => DecoderSourceScore(row, encoder))
                    // Prefer the newest listener while an N60 HB-only restart's
                    // retired advertisement is still expiring from discovery.
                    .ThenByDescending(DiscoveredSourcePort)
                    .FirstOrDefault();
                if (match.ValueKind == JsonValueKind.Object) found[encoder.Id] = match.Clone();
            }
        }
        if (found.Count != encoders.Count)
        {
            var missing = encoders.Where(encoder => !found.ContainsKey(encoder.Id)).Select(encoder => encoder.Hostname);
            throw new DeviceApiException($"The N60 could not discover encoder feed(s): {string.Join(", ", missing)}. Confirm each NDI sender is running and visible to the selected Discovery Server.");
        }
        return found;
    }

    private static int DecoderSourceScore(JsonElement source, ManagedDevice encoder)
    {
        var ipMatch = string.Equals(String(source, "ip"), encoder.IpAddress, StringComparison.Ordinal);
        var channelMatch = !string.IsNullOrWhiteSpace(encoder.NdiChannelName) &&
            string.Equals(String(source, "channel_name").Trim(), encoder.NdiChannelName.Trim(), StringComparison.OrdinalIgnoreCase);
        var deviceMatch = string.Equals(String(source, "device_name").Trim(), encoder.Hostname.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!ipMatch && !channelMatch && !deviceMatch) return 0;
        return (channelMatch ? 8 : 0) + (deviceMatch ? 4 : 0) + (ipMatch ? 2 : 0);
    }

    private static int DiscoveredSourcePort(JsonElement source)
    {
        var port = Number(source, "port", Number(source, "listener_port", 0));
        if (port > 0) return port;
        var url = String(source, "original_url", String(source, "url"));
        return Uri.TryCreate($"tcp://{url}", UriKind.Absolute, out var parsed) ? parsed.Port : 0;
    }

    public async Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        await SetIdentityAndDiscoveryAsync(client, "", channelName, group, null, ct);
    }

    public async Task SetHostnameAsync(string hostname, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        using var changed = await GetAsync(client, $"/api/systemctrl/system/setHostname?name={Uri.EscapeDataString(hostname)}", "set N60 hostname", ct);
    }

    public async Task ConfigureMulticastAsync(MulticastDeviceConfiguration settings, CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        var mode = (await client.GetStringAsync("/api/codec/mode/get", ct)).Trim().Trim('"');
        if (string.Equals(mode, "encode", StringComparison.OrdinalIgnoreCase))
        {
            if (settings.NetPrefix is null || settings.Netmask is null)
                throw new ArgumentException("An N60 encoder requires a multicast prefix and subnet mask.");
            await EnsureHbOnlyEncodingAsync(client, ct);
            const string streamKey = "main_full";
            const string streamType = "ndi-full";
            var getPath = $"/api/codec/streams/{streamKey}/{streamType}/get";
            var configuredSuccessfully = false;
            DeviceApiException? configurationError = null;
            for (var attempt = 0; attempt < 6 && !configuredSuccessfully; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
                try
                {
                    using var configured = await PostAsync(client, $"/api/codec/streams/{streamKey}/{streamType}/set", new
                    {
                        connection = "multicast",
                        netprefix = settings.NetPrefix,
                        netmask = settings.Netmask,
                        // N60 firmware silently replaces a JSON number with its
                        // default TTL (127); its own UI submits this as a string.
                        ttl = settings.Ttl.ToString(),
                        types = streamType
                    }, "configure N60 ndi-full multicast sender", ct);
                    configuredSuccessfully = true;
                }
                catch (DeviceApiException ex) when (attempt < 5
                    && ex.Message.Contains("Request too often", StringComparison.OrdinalIgnoreCase))
                {
                    configurationError = ex;
                }
            }
            if (!configuredSuccessfully)
                throw configurationError ?? new DeviceApiException("N60 ndi-full multicast configuration did not complete.");

            var retained = false;
            for (var attempt = 0; attempt < 8 && !retained; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                using var verified = await GetAsync(client, getPath, "verify N60 ndi-full multicast sender", ct);
                var data = Payload(verified.RootElement);
                retained = string.Equals(String(data, "connection"), "multicast", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(String(data, "netprefix"), settings.NetPrefix, StringComparison.Ordinal)
                    && string.Equals(String(data, "netmask"), settings.Netmask, StringComparison.Ordinal)
                    && Number(data, "ttl", -1) == settings.Ttl;
            }
            if (!retained)
                throw new DeviceApiException("N60 ndi-full did not retain its multicast allocation.");
            await RestartHbSenderAsync(client, ct);
            return;
        }

        using var connection = await PostAsync(client, "/api/codec/decode/setConnection",
            new { ndi_connection = "multicast" },
            "set N60 decoder multicast receive mode",
            ct);
        using var verifiedConnection = await GetAsync(client, "/api/codec/decode/get",
            "verify N60 decoder multicast receive mode",
            ct);
        var connectionData = verifiedConnection.RootElement.TryGetProperty("data", out var wrappedConnection)
            ? wrappedConnection
            : verifiedConnection.RootElement;
        if (!string.Equals(String(connectionData, "ndi_connection"), "multicast", StringComparison.OrdinalIgnoreCase))
            throw new DeviceApiException("The N60 decoder did not retain multicast receive mode.");
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
            using var verifiedConnection = await GetAsync(client, "/api/codec/decode/get",
                "verify N60 decoder unicast receive mode",
                ct);
            var connectionData = verifiedConnection.RootElement.TryGetProperty("data", out var wrappedConnection)
                ? wrappedConnection
                : verifiedConnection.RootElement;
            if (!string.Equals(String(connectionData, "ndi_connection"), "unicast", StringComparison.OrdinalIgnoreCase))
                throw new DeviceApiException("The N60 decoder did not retain unicast receive mode.");
            return;
        }

        await EnsureHbOnlyEncodingAsync(client, ct);
        const string stream = "main_full";
        const string type = "ndi-full";
        using (var configured = await PostAsync(client, $"/api/codec/streams/{stream}/{type}/set", new
        {
            connection = "unicast",
            types = type
        }, "configure N60 ndi-full unicast sender", ct))
        {
        }
        using var verified = await GetAsync(client, $"/api/codec/streams/{stream}/{type}/get", "verify N60 ndi-full unicast sender", ct);
        var data = Payload(verified.RootElement);
        if (!string.Equals(String(data, "connection"), "unicast", StringComparison.OrdinalIgnoreCase))
            throw new DeviceApiException("N60 ndi-full did not confirm unicast mode.");
        await RestartHbSenderAsync(client, ct);
    }

    public async Task BlankAsync(CancellationToken ct)
    {
        using var client = await AuthorizedAsync(ct);
        await RetryDecoderMutationAsync(
            () => SelectBlankPresetAsync(client, ct),
            "blank N60 output",
            ct);
        await RetryDecoderMutationAsync(
            () => SetBlankColorAsync(client, ct),
            "set N60 blank colour",
            ct);

        Exception? lastReadError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            try
            {
                using var current = await GetAsync(client, "/api/codec/decode/get", "verify N60 blank output", ct);
                var data = Payload(current.RootElement);
                var name = String(data, "name").Trim();
                var url = String(data, "url").Trim();
                var address = String(data, "ip").Trim();
                if (string.IsNullOrEmpty(name) &&
                    string.IsNullOrEmpty(url) &&
                    (string.IsNullOrEmpty(address) || address == "0.0.0.0"))
                    return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is DeviceApiException or HttpRequestException or TaskCanceledException)
            {
                lastReadError = ex;
            }
        }

        throw new DeviceApiException("N60 accepted the blank preset but did not confirm an empty decoder source.", lastReadError);
    }

    private async Task SetBlankColorAsync(HttpClient client, CancellationToken ct)
    {
        ApplyCookies(client);
        // This endpoint is case-sensitive. PostAsJsonAsync's web defaults turn
        // an anonymous BlankColor property into "blankColor", which firmware
        // 2.45 rejects with HTTP 422. Dictionary keys retain the API's exact
        // documented spelling.
        using var response = await client.PostAsJsonAsync(
            "/api/codec/preset/set_blank_color",
            new Dictionary<string, string> { ["BlankColor"] = "#000000" },
            ct);
        using var accepted = await ReadJsonAsync(response, "set N60 blank colour", ct);
    }

    private async Task SelectBlankPresetAsync(HttpClient client, CancellationToken ct)
    {
        ApplyCookies(client);
        using var response = await client.PostAsJsonAsync("/api/codec/decode/add", new { id = 0 }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new DeviceApiException($"blank N60 output failed with HTTP {(int)response.StatusCode}.");

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("result", out var result))
                return;

            var value = result.ToString();
            // Firmware 2.45 returns {"result":"","msg":""} after it has
            // successfully selected preset zero. Other N60 mutations use
            // "ok", so accept both documented success representations here.
            if (string.IsNullOrWhiteSpace(value) || value.Equals("ok", StringComparison.OrdinalIgnoreCase))
                return;

            var message = document.RootElement.TryGetProperty("msg", out var msg) ? msg.ToString() : value;
            throw new DeviceApiException($"blank N60 output was rejected by the device: {message}");
        }
        catch (JsonException ex)
        {
            throw new DeviceApiException("blank N60 output returned an invalid response.", ex);
        }
    }

    private static async Task RetryDecoderMutationAsync(
        Func<Task> mutation,
        string operation,
        CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await mutation();
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is DeviceApiException or HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
                if (attempt < 5) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
        }

        throw new DeviceApiException($"{operation} failed after 5 attempts.", lastError);
    }
}
