using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text.Json;

namespace KiloviewSetup.Core;

/// <summary>Validated against the KiloLink Server Pro 1.08.0034 web API.</summary>
public sealed class KiloLinkServerClient(IHttpClientFactory clients)
{
    private readonly SemaphoreSlim _deviceMutationGate = new(1, 1);

    public async Task<IReadOnlyList<KiloLinkServerDiscovery>> DiscoverAsync(
        int webPort,
        LocalNetworkInterface network,
        CancellationToken ct)
    {
        if (webPort is < 1 or > 65535) throw new ArgumentException("KiloLink web port is invalid.");
        var found = new ConcurrentDictionary<string, KiloLinkServerDiscovery>(StringComparer.OrdinalIgnoreCase);
        var addresses = NetworkAddressing.ExpandCidr(NetworkAddressing.GetScanCidr(network)).ToArray();
        await Parallel.ForEachAsync(addresses, new ParallelOptions
        {
            MaxDegreeOfParallelism = NetworkAddressing.DiscoveryParallelism(addresses.Length),
            CancellationToken = ct
        }, async (address, token) =>
        {
            using var client = clients.CreateClient("KiloLinkDiscovery");
            client.BaseAddress = new Uri($"http://{address}:{webPort}/");
            client.Timeout = TimeSpan.FromMilliseconds(850);
            try
            {
                using var response = await client.GetAsync("api/user/version_info.json", token);
                if (!response.IsSuccessStatusCode) return;
                using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                if (!body.RootElement.TryGetProperty("result", out var result) || !string.Equals(result.GetString(), "ok", StringComparison.OrdinalIgnoreCase)) return;
                var version = Data(body).TryGetProperty("version", out var value) ? value.GetString() ?? "unknown" : "unknown";
                using var signature = await client.GetAsync("static/config.js", token);
                var config = await signature.Content.ReadAsStringAsync(token);
                if (!signature.IsSuccessStatusCode || !config.Contains("KiloLink Server", StringComparison.OrdinalIgnoreCase)) return;
                found[address.ToString()] = new(address.ToString(), webPort, version);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        });
        return found.Values.OrderBy(x => NetworkAddressing.ToUInt(System.Net.IPAddress.Parse(x.ServerIp))).ToArray();
    }

    public async Task<KiloLinkConnectionStatus> TestAsync(string serverIp, int webPort, KiloLinkCredential credential, CancellationToken ct)
    {
        using var session = await LoginAsync(serverIp, webPort, credential, ct);
        using var version = await GetAsync(session.Client, "api/user/version_info.json", ct);
        using var firmwareTypes = await PostAsync(session.Client, "api/tools/getFirmwareTypes.json", new { }, ct);
        using var deviceTypes = await PostAsync(session.Client, "api/tools/getDeviceTypes.json", new { dn = "", @virtual = false }, ct);
        using var devices = await PostAsync(session.Client, "api/tools/getDeviceList.json", new { dn = "", @virtual = false }, ct);
        var versionText = Data(version).TryGetProperty("version", out var value) ? value.GetString() ?? "unknown" : "unknown";
        return new(versionText, ReadStrings(Data(deviceTypes)), ReadStrings(Data(firmwareTypes), "list"), CountObjects(Data(devices), "list"));
    }

    public async Task<bool> AuthenticateAsync(string serverIp, int webPort, KiloLinkCredential credential, CancellationToken ct)
    {
        using var session = await LoginAsync(serverIp, webPort, credential, ct);
        return session.Identity.PasswordChanged;
    }

    public async Task ChangeInitialPasswordAsync(
        string serverIp,
        int webPort,
        KiloLinkCredential factoryCredential,
        string newPassword,
        CancellationToken ct)
    {
        using var session = await LoginAsync(serverIp, webPort, factoryCredential, ct);
        var identity = session.Identity;
        if (string.IsNullOrWhiteSpace(identity.Dn) ||
            string.IsNullOrWhiteSpace(identity.Uid) ||
            string.IsNullOrWhiteSpace(identity.Cn))
            throw new InvalidOperationException("KiloLink Server did not return the administrator identity required for initial password setup.");

        var dn = Uri.EscapeDataString(identity.Dn);
        var uid = Uri.EscapeDataString(identity.Uid);
        var cn = Uri.EscapeDataString(identity.Cn);
        var type = Uri.EscapeDataString(string.IsNullOrWhiteSpace(identity.Type) ? "user" : identity.Type);
        var encodedPassword = Uri.EscapeDataString(newPassword);
        using var response = await PostAsync(session.Client, "api/tools/changeSelfPassword.json", new
        {
            dn,
            type,
            cfg = new
            {
                dn,
                uid,
                cn,
                userPassword = encodedPassword,
                confirmNewPassword = encodedPassword,
                type
            }
        }, ct);
    }

    public async Task<KiloLinkAuthorizationResult> AuthorizeDeviceAsync(
        string serverIp,
        int webPort,
        KiloLinkCredential credential,
        string serialNumber,
        string hostname,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serialNumber)) throw new ArgumentException("A device serial number is required for KiloLink authorization.");
        if (string.IsNullOrWhiteSpace(hostname)) throw new ArgumentException("A device hostname is required for the KiloLink alias.");
        await _deviceMutationGate.WaitAsync(ct);
        try
        {
            using var session = await LoginAsync(serverIp, webPort, credential, ct);
            using var devicesResponse = await PostAsync(session.Client, "api/tools/getDeviceList.json", new { dn = "", @virtual = false }, ct);
            var existing = Flatten(Data(devicesResponse), "list").FirstOrDefault(row => ContainsNormalizedValue(row, serialNumber));
            if (existing.ValueKind == JsonValueKind.Object)
            {
                var code = GetStringDeep(existing, "description");
                var currentName = GetStringDeep(existing, "cn") ?? "";
                if (string.IsNullOrWhiteSpace(code))
                {
                    code = await CreateAuthorizationCodeAsync(session.Client, ct);
                    await ModifyDeviceAsync(session.Client, existing, hostname, serialNumber, code, ct);
                }
                else if (!string.Equals(currentName, hostname, StringComparison.Ordinal))
                {
                    await RenameDeviceAsync(session.Client, existing, hostname, ct);
                }
                return new(serialNumber, hostname, code, false);
            }

            using var treeResponse = await PostWithoutBodyAsync(session.Client, "api/tools/searchFix.json", ct);
            var parentDn = await FindOrCreateDeviceGroupAsync(session.Client, Data(treeResponse), hostname, ct);
            var generated = await CreateAuthorizationCodeAsync(session.Client, ct);
            using var added = await PostAsync(session.Client, "api/tools/add.json", new
            {
                dn = Uri.EscapeDataString(parentDn),
                type = "device",
                cfg = new
                {
                    cn = Uri.EscapeDataString(hostname),
                    serialNumber = Uri.EscapeDataString(serialNumber),
                    description = Uri.EscapeDataString(generated),
                    o = ""
                }
            }, ct);
            return new(serialNumber, hostname, generated, true);
        }
        finally
        {
            _deviceMutationGate.Release();
        }
    }

    public async Task<KiloLinkClearResult> ClearDeviceInventoryAsync(
        string serverIp,
        int webPort,
        KiloLinkCredential credential,
        CancellationToken ct)
    {
        using var session = await LoginAsync(serverIp, webPort, credential, ct);
        using var devicesResponse = await PostAsync(session.Client, "api/tools/getDeviceList.json", new { dn = "", @virtual = false }, ct);
        var deviceDns = Flatten(Data(devicesResponse), "list")
            .Select(row => GetString(row, "dn"))
            .Where(dn => !string.IsNullOrWhiteSpace(dn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        if (deviceDns.Length > 0)
        {
            using var deleted = await PostAsync(session.Client, "api/tools/deleteMany.json",
                new { list = deviceDns.Select(Uri.EscapeDataString).ToArray() }, ct);
        }

        using var treeResponse = await PostWithoutBodyAsync(session.Client, "api/tools/searchFix.json", ct);
        const string rootDn = "ou=KVDevices,cn=admin,dc=kiloview,dc=com";
        var groupDns = Flatten(Data(treeResponse), "children")
            .Where(row => string.Equals(GetStringDeep(row, "type"), "organization", StringComparison.OrdinalIgnoreCase))
            .Select(row => GetStringDeep(row, "dn"))
            .Where(dn => !string.IsNullOrWhiteSpace(dn)
                && !string.Equals(dn, rootDn, StringComparison.OrdinalIgnoreCase)
                && dn.Contains(rootDn, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .OrderByDescending(dn => dn.Count(ch => ch == ','))
            .ToArray();
        foreach (var dn in groupDns)
        {
            using var deleted = await PostAsync(session.Client, "api/tools/delete.json",
                new { dn = Uri.EscapeDataString(dn) }, ct);
        }

        return new(deviceDns.Length, groupDns.Length);
    }

    public async Task<bool> RemoveDeviceAsync(
        string serverIp,
        int webPort,
        KiloLinkCredential credential,
        string serialNumber,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serialNumber)) throw new ArgumentException("A device serial number is required for KiloLink removal.");
        using var session = await LoginAsync(serverIp, webPort, credential, ct);
        using var devicesResponse = await PostAsync(session.Client, "api/tools/getDeviceList.json", new { dn = "", @virtual = false }, ct);
        var dns = Flatten(Data(devicesResponse), "list")
            .Where(row => ContainsNormalizedValue(row, serialNumber))
            .Select(row => GetStringDeep(row, "dn"))
            .Where(dn => !string.IsNullOrWhiteSpace(dn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        if (dns.Length == 0) return false;
        using var deleted = await PostAsync(session.Client, "api/tools/deleteMany.json",
            new { list = dns.Select(Uri.EscapeDataString).ToArray() }, ct);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var verification = await PostAsync(session.Client, "api/tools/getDeviceList.json", new { dn = "", @virtual = false }, ct);
            if (!Flatten(Data(verification), "list").Any(row => ContainsNormalizedValue(row, serialNumber))) return true;
            if (attempt < 4) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        throw new InvalidOperationException("KiloLink still lists the Kiloview after accepting its removal request.");
    }

    public async Task<KiloLinkFleetResult> DispatchFleetAsync(
        string serverIp,
        int webPort,
        KiloLinkCredential credential,
        IReadOnlyList<FirmwarePackage> packages,
        IReadOnlyList<ManagedDevice> managedDevices,
        CancellationToken ct)
    {
        using var session = await LoginAsync(serverIp, webPort, credential, ct);
        using var version = await GetAsync(session.Client, "api/user/version_info.json", ct);
        var serverVersion = Data(version).TryGetProperty("version", out var value) ? value.GetString() ?? "" : "";
        if (!serverVersion.StartsWith("1.08", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"KiloLink Server {serverVersion} has not been validated for automated fleet firmware updates. Expected the 1.08 API contract.");

        var dispatched = 0;
        var uploaded = 0;
        var models = new List<string>();
        foreach (var package in packages)
        {
            var model = package.Model.ToUpperInvariant();
            var intended = managedDevices.Where(d => ModelOf(d) == model).ToArray();
            var targets = await WaitForTargetsAsync(session.Client, model, intended, ct);
            if (targets.Count == 0) throw new InvalidOperationException($"KiloLink has no registered {model} devices matching this onboarding job.");

            var description = $"Kiloview Setup {model} {package.Sha256[..Math.Min(12, package.Sha256.Length)]}";
            await UploadAsync(session.Client, package, description, ct);
            uploaded++;
            var firmware = await FindFirmwareAsync(session.Client, model, description, ct);
            using var dispatch = await PostAsync(session.Client, "api/tools/upgradeDevices.json", new
            {
                list = targets,
                version = firmware.Version,
                device_type = model,
                type = firmware.Type
            }, ct);
            dispatched += targets.Count;
            models.Add(model);
        }
        return new(uploaded, dispatched, models);
    }

    private static async Task<List<string>> WaitForTargetsAsync(HttpClient client, string model, IReadOnlyList<ManagedDevice> intended, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 13; attempt++)
        {
            using var response = await PostAsync(client, "api/tools/getDeviceList.json", new { dn = "", device_type = model, @virtual = false }, ct);
            var rows = Flatten(Data(response), "list").Where(x => GetString(x, "dn") is not null).ToArray();
            var identifiers = intended.SelectMany(d => new[] { d.Id, d.MacAddress, d.Hostname, d.IpAddress }).Select(Normalize).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matched = rows.Where(row => row.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && identifiers.Contains(Normalize(p.Value.GetString() ?? ""))))
                .Select(row => GetString(row, "dn")!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matched.Count == intended.Count) return matched;
            if (attempt < 12) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new InvalidOperationException($"KiloLink did not report every onboarded {model} device within 60 seconds. Confirm the devices are online and registered before retrying.");
    }

    private static async Task UploadAsync(HttpClient client, FirmwarePackage package, string description, CancellationToken ct)
    {
        await using var file = File.OpenRead(package.LocalPath);
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"\"",
            FileName = $"\"{package.FileName.Replace("\"", "")}\""
        };
        form.Add(content);
        form.Add(new StringContent(description), "desc");
        using var response = await client.PostAsync("api/tools/uploadFirmware.json", form, ct);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        EnsureOk(body);
    }

    private static async Task<(string Version, string Type)> FindFirmwareAsync(HttpClient client, string model, string description, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await PostAsync(client, "api/tools/getFirmwareList.json", new { search = "", page = 1, limit = 100000, device_type = model }, ct);
            var data = Data(response);
            if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array && info.GetArrayLength() > 0)
            {
                var root = info[0];
                var type = GetString(root, "type") ?? model;
                var firmware = Flatten(root, "children").FirstOrDefault(x => string.Equals(GetString(x, "desc"), description, StringComparison.Ordinal));
                var version = firmware.ValueKind == JsonValueKind.Object ? GetString(firmware, "version") : null;
                if (!string.IsNullOrWhiteSpace(version)) return (version, type);
            }
            if (attempt < 9) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new InvalidOperationException($"KiloLink accepted the {model} upload but did not list the package afterward.");
    }

    private async Task<Session> LoginAsync(string serverIp, int webPort, KiloLinkCredential credential, CancellationToken ct)
    {
        InputValidation.Ip(serverIp, "KiloLink Server IP");
        if (webPort is < 1 or > 65535) throw new ArgumentException("KiloLink web port is invalid.");
        var client = clients.CreateClient("KiloLinkServer");
        client.BaseAddress = new Uri($"http://{serverIp}:{webPort}/");
        client.Timeout = TimeSpan.FromMinutes(20);
        try
        {
            using var response = await client.PostAsJsonAsync("api/tools/login.json", new { username = credential.Username, password = credential.Password }, ct);
            response.EnsureSuccessStatusCode();
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            EnsureOk(body);
            var data = Data(body);
            var header = new Dictionary<string, object?> { ["username"] = credential.Username };
            foreach (var property in data.EnumerateObject()) header[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
            header["alias"] = GetString(data, "cn") ?? credential.Username;
            header["language"] = "en";
            header["software"] = "true";
            client.DefaultRequestHeaders.TryAddWithoutValidation("app", JsonSerializer.Serialize(header));
            return new Session(client, new(
                GetString(data, "dn") ?? "",
                GetString(data, "uid") ?? credential.Username,
                GetString(data, "cn") ?? credential.Username,
                GetString(data, "type") ?? "user",
                GetBoolean(data, "changed")));
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<string> CreateAuthorizationCodeAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await PostWithoutBodyAsync(client, "api/tools/createRandomKey.json", ct);
        var data = Data(response);
        var code = data.ValueKind == JsonValueKind.String ? data.GetString() : GetString(data, "key") ?? GetString(data, "code");
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("KiloLink Server did not return a device authorization code.");
        return code;
    }

    private static async Task RenameDeviceAsync(HttpClient client, JsonElement existing, string hostname, CancellationToken ct)
    {
        var dn = GetStringDeep(existing, "dn") ?? throw new InvalidOperationException("The existing KiloLink device record has no DN.");
        var type = GetStringDeep(existing, "type") ?? "device";
        var cfg = EncodedDeviceConfig(existing, hostname, GetStringDeep(existing, "serialNumber") ?? "", GetStringDeep(existing, "description") ?? "");
        using var response = await PostAsync(client, "api/tools/rename.json", new
        {
            dn = Uri.EscapeDataString(dn),
            type = Uri.EscapeDataString(type),
            cfg,
            @new = Uri.EscapeDataString(hostname)
        }, ct);
    }

    private static async Task ModifyDeviceAsync(HttpClient client, JsonElement existing, string hostname, string serial, string code, CancellationToken ct)
    {
        var dn = GetStringDeep(existing, "dn") ?? throw new InvalidOperationException("The existing KiloLink device record has no DN.");
        var type = GetStringDeep(existing, "type") ?? "device";
        var cfg = EncodedDeviceConfig(existing, hostname, serial, code);
        using var response = await PostAsync(client, "api/tools/modify.json", new
        {
            dn = Uri.EscapeDataString(dn),
            type = Uri.EscapeDataString(type),
            cfg
        }, ct);
    }

    private static object EncodedDeviceConfig(JsonElement existing, string hostname, string serial, string code) => new
    {
        cn = Uri.EscapeDataString(hostname),
        serialNumber = Uri.EscapeDataString(serial),
        description = Uri.EscapeDataString(code),
        o = Uri.EscapeDataString(GetStringDeep(existing, "o") ?? "")
    };

    private static string FindDefaultDeviceGroup(JsonElement tree)
    {
        var rows = FlattenAny(tree).Where(x => x.ValueKind == JsonValueKind.Object).ToArray();
        var dns = rows.Select(x => GetStringDeep(x, "dn")).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
        return dns.FirstOrDefault(x => x.StartsWith("ou=1,ou=KVDevices,", StringComparison.OrdinalIgnoreCase))
            ?? dns.FirstOrDefault(x => x.StartsWith("ou=", StringComparison.OrdinalIgnoreCase) && x.Contains(",ou=KVDevices,", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("KiloLink Server has no real device group under KVDevices.");
    }

    private static async Task<string> FindOrCreateDeviceGroupAsync(HttpClient client, JsonElement tree, string hostname, CancellationToken ct)
    {
        try { return FindDefaultDeviceGroup(tree); }
        catch (InvalidOperationException)
        {
            var marker = hostname.LastIndexOf("-KV-", StringComparison.OrdinalIgnoreCase);
            var groupName = marker > 0 ? hostname[..marker] : "Onboarded";
            using var added = await PostAsync(client, "api/tools/add.json", new
            {
                dn = Uri.EscapeDataString("ou=KVDevices,cn=admin,dc=kiloview,dc=com"),
                ou = Uri.EscapeDataString(groupName),
                type = "organization",
                cfg = new { ou = Uri.EscapeDataString(groupName) },
                cn = "",
                nodeName = ""
            }, ct);
            using var refreshed = await PostWithoutBodyAsync(client, "api/tools/searchFix.json", ct);
            return FindDefaultDeviceGroup(Data(refreshed));
        }
    }

    private static bool ContainsNormalizedValue(JsonElement value, string expected)
    {
        var target = Normalize(expected);
        if (value.ValueKind == JsonValueKind.String) return Normalize(value.GetString() ?? "") == target;
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(x => ContainsNormalizedValue(x, expected));
        return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any(p => ContainsNormalizedValue(p.Value, expected));
    }

    private static string? GetStringDeep(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String) return direct.GetString();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var nested = GetStringDeep(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static IEnumerable<JsonElement> FlattenAny(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) foreach (var nested in FlattenAny(item)) yield return nested;
            yield break;
        }
        if (value.ValueKind != JsonValueKind.Object) yield break;
        yield return value;
        foreach (var property in value.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                foreach (var nested in FlattenAny(property.Value)) yield return nested;
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        try { EnsureOk(body); return body; }
        catch { body.Dispose(); throw; }
    }

    private static async Task<JsonDocument> PostAsync(HttpClient client, string path, object value, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, value, ct);
        response.EnsureSuccessStatusCode();
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        try { EnsureOk(body); return body; }
        catch { body.Dispose(); throw; }
    }

    private static async Task<JsonDocument> PostWithoutBodyAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.PostAsync(path, null, ct);
        response.EnsureSuccessStatusCode();
        var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        try { EnsureOk(body); return body; }
        catch { body.Dispose(); throw; }
    }

    private static void EnsureOk(JsonDocument body)
    {
        var root = body.RootElement;
        var failed = root.TryGetProperty("result", out var result) && string.Equals(result.GetString(), "error", StringComparison.OrdinalIgnoreCase);
        if (!failed && root.TryGetProperty("Result", out var legacy) && legacy.ValueKind == JsonValueKind.Number) failed = legacy.GetInt32() == 400;
        if (failed)
        {
            var message = root.TryGetProperty("msg", out var msg) ? msg.ToString() : root.TryGetProperty("Status", out var status) ? status.ToString() : "Unknown KiloLink error";
            throw new InvalidOperationException($"KiloLink Server rejected the request: {message}");
        }
    }

    private static JsonElement Data(JsonDocument body) => body.RootElement.TryGetProperty("data", out var data) ? data : body.RootElement;
    private static string? GetString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static bool GetBoolean(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };
    }
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string ModelOf(ManagedDevice device) => device.Model.StartsWith("N60", StringComparison.OrdinalIgnoreCase) ? "N60" : "N6";

    private static IReadOnlyList<string> ReadStrings(JsonElement value, string? property = null)
    {
        if (property is not null && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var nested)) value = nested;
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
    }

    private static int CountObjects(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var nested)) return 0;
        return nested.ValueKind == JsonValueKind.Array ? nested.GetArrayLength() : 0;
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement value, string property)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var nested)) value = nested;
        if (value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in value.EnumerateArray())
        {
            yield return item;
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var child in Flatten(item, "children")) yield return child;
        }
    }

    private sealed record LoginIdentity(string Dn, string Uid, string Cn, string Type, bool PasswordChanged);

    private sealed class Session(HttpClient client, LoginIdentity identity) : IDisposable
    {
        public HttpClient Client { get; } = client;
        public LoginIdentity Identity { get; } = identity;
        public void Dispose() => Client.Dispose();
    }
}
