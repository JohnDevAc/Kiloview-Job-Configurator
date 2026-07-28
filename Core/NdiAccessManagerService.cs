using System.Text.Json;
using System.Text.Json.Nodes;

namespace KiloviewSetup.Core;

public sealed record NdiAccessManagerStatus(
    bool Detected,
    bool Configured,
    bool InUse,
    string ConfigPath,
    string? NetPrefix,
    string? Netmask,
    int? Ttl);

public sealed class NdiAccessManagerService
{
    private readonly string _configPath = Environment.GetEnvironmentVariable("KILOVIEW_NDI_CONFIG_PATH") is { Length: > 0 } overridePath
        ? Path.GetFullPath(overridePath)
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NDI",
            "ndi-config.v1.json");

    public string ConfigPath => _configPath;

    public bool Detected => File.Exists(_configPath) || AccessManagerCandidates().Any(File.Exists);

    public async Task<NdiAccessManagerStatus> ApplyAsync(
        string netPrefix,
        string netmask,
        int ttl,
        string group,
        string discoveryServer,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_configPath)
            ?? throw new InvalidOperationException("The NDI Access Manager configuration directory could not be resolved.");
        Directory.CreateDirectory(directory);

        JsonObject root;
        if (File.Exists(_configPath))
        {
            try
            {
                await using var input = File.OpenRead(_configPath);
                root = await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject
                    ?? throw new JsonException("The configuration root is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"NDI Access Manager configuration at '{_configPath}' is not valid JSON. Open Access Manager once to repair it before applying multicast settings.",
                    ex);
            }

            File.Copy(_configPath, _configPath + ".kiloview-backup", true);
        }
        else
        {
            root = new JsonObject();
        }

        var ndi = Object(root, "ndi");
        var groups = Object(ndi, "groups");
        groups["send"] = AddGroup(Text(groups, "send"), group);
        groups["recv"] = AddGroup(Text(groups, "recv"), group);

        var networks = Object(ndi, "networks");
        if (!string.IsNullOrWhiteSpace(discoveryServer))
            networks["discovery"] = discoveryServer.Trim();

        var multicast = Object(ndi, "multicast");
        var send = Object(multicast, "send");
        send["enable"] = true;
        send["netprefix"] = netPrefix;
        send["netmask"] = netmask;
        send["ttl"] = ttl;
        var receive = Object(multicast, "recv");
        receive["enable"] = true;
        receive["subnets"] ??= new JsonArray();

        var temporary = Path.Combine(directory, $".ndi-config.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine,
                ct);
            await using (var verificationStream = File.OpenRead(temporary))
                _ = await JsonNode.ParseAsync(verificationStream, cancellationToken: ct)
                    ?? throw new InvalidOperationException("The generated NDI Access Manager configuration could not be validated.");
            File.Move(temporary, _configPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return await ReadStatusAsync(netPrefix, netmask, ttl, ct);
    }

    public async Task<NdiAccessManagerStatus> ReadStatusAsync(
        string? expectedPrefix = null,
        string? expectedMask = null,
        int? expectedTtl = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(_configPath))
            return new(Detected, false, false, _configPath, null, null, null);

        try
        {
            await using var input = File.OpenRead(_configPath);
            var root = await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject;
            var send = root?["ndi"]?["multicast"]?["send"] as JsonObject;
            var enabled = Bool(send, "enable");
            var prefix = Text(send, "netprefix");
            var mask = Text(send, "netmask");
            var ttl = Int(send, "ttl");
            var matches = enabled
                && (expectedPrefix is null || string.Equals(prefix, expectedPrefix, StringComparison.Ordinal))
                && (expectedMask is null || string.Equals(mask, expectedMask, StringComparison.Ordinal))
                && (expectedTtl is null || ttl == expectedTtl);
            return new(Detected, matches, matches, _configPath, prefix, mask, ttl);
        }
        catch (JsonException)
        {
            return new(Detected, false, false, _configPath, null, null, null);
        }
    }

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static string AddGroup(string? current, string group) => string.Join(
        ",",
        (current ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(group.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string? Text(JsonObject? source, string name) =>
        source?[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static bool Bool(JsonObject? source, string name) =>
        source?[name] is JsonValue value
        && (value.TryGetValue<bool>(out var result) && result
            || value.TryGetValue<string>(out var text) && bool.TryParse(text, out result) && result);

    private static int? Int(JsonObject? source, string name)
    {
        if (source?[name] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : null;
    }

    private static IEnumerable<string> AccessManagerCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles)) yield break;
        yield return Path.Combine(programFiles, "NDI", "NDI 6 Tools", "Access Manager.exe");
        yield return Path.Combine(programFiles, "NDI", "NDI Tools", "Access Manager.exe");
        yield return Path.Combine(programFiles, "NewTek", "NDI 5 Tools", "Access Manager.exe");
        yield return Path.Combine(programFiles, "NewTek", "NDI 4 Tools", "Access Manager.exe");
    }
}
