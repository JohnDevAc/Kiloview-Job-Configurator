using System.Diagnostics;
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
    int? Ttl,
    bool AccessManagerRunning = false,
    string? Error = null);

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
    public bool IsRunning => AccessManagerProcessNames().Any(name =>
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    public async Task<NdiAccessManagerStatus> ApplyAsync(
        string netPrefix,
        string netmask,
        int ttl,
        string group,
        string discoveryServer,
        CancellationToken ct)
    {
        if (IsRunning)
            throw new InvalidOperationException(
                "Close NDI Access Manager before applying multicast settings. It keeps an in-memory copy and can overwrite externally applied changes when it exits.");

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

        return await ReadStatusAsync(netPrefix, netmask, ttl, ct, group, discoveryServer);
    }

    public async Task<NdiAccessManagerStatus> DisableMulticastAsync(CancellationToken ct)
    {
        if (IsRunning)
            throw new InvalidOperationException(
                "Close NDI Access Manager before reverting to unicast. It keeps an in-memory copy and can overwrite externally applied changes when it exits.");

        if (!File.Exists(_configPath))
            return new(Detected, false, false, _configPath, null, null, null);

        JsonObject root;
        try
        {
            await using var input = File.OpenRead(_configPath);
            root = await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject
                ?? throw new JsonException("The configuration root is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"NDI Access Manager configuration at '{_configPath}' is not valid JSON. Open Access Manager once to repair it before reverting to unicast.",
                ex);
        }

        var multicast = Object(Object(root, "ndi"), "multicast");
        Object(multicast, "send")["enable"] = false;
        Object(multicast, "recv")["enable"] = false;

        var directory = Path.GetDirectoryName(_configPath)
            ?? throw new InvalidOperationException("The NDI Access Manager configuration directory could not be resolved.");
        File.Copy(_configPath, _configPath + ".kiloview-backup", true);
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

        var status = await ReadStatusAsync(ct: ct);
        if (status.InUse)
            throw new InvalidOperationException("NDI Access Manager still reports multicast as enabled.");
        return status;
    }

    public async Task<NdiAccessManagerStatus> ReadStatusAsync(
        string? expectedPrefix = null,
        string? expectedMask = null,
        int? expectedTtl = null,
        CancellationToken ct = default,
        string? expectedGroup = null,
        string? expectedDiscoveryServer = null)
    {
        var running = IsRunning;
        if (!File.Exists(_configPath))
            return new(Detected, false, false, _configPath, null, null, null, running, "The NDI Access Manager configuration file is missing.");

        try
        {
            await using var input = File.OpenRead(_configPath);
            var root = await JsonNode.ParseAsync(input, cancellationToken: ct) as JsonObject;
            var send = root?["ndi"]?["multicast"]?["send"] as JsonObject;
            var receive = root?["ndi"]?["multicast"]?["recv"] as JsonObject;
            var groups = root?["ndi"]?["groups"] as JsonObject;
            var networks = root?["ndi"]?["networks"] as JsonObject;
            var enabled = Bool(send, "enable");
            var prefix = Text(send, "netprefix");
            var mask = Text(send, "netmask");
            var ttl = Int(send, "ttl");
            var matches = enabled
                && Bool(receive, "enable")
                && (expectedPrefix is null || string.Equals(prefix, expectedPrefix, StringComparison.Ordinal))
                && (expectedMask is null || string.Equals(mask, expectedMask, StringComparison.Ordinal))
                && (expectedTtl is null || ttl == expectedTtl)
                && (expectedGroup is null
                    || ContainsValue(Text(groups, "send"), expectedGroup)
                    && ContainsValue(Text(groups, "recv"), expectedGroup))
                && (string.IsNullOrWhiteSpace(expectedDiscoveryServer)
                    || ContainsValue(Text(networks, "discovery"), expectedDiscoveryServer));
            return new(Detected, matches, matches, _configPath, prefix, mask, ttl, running);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(Detected, false, false, _configPath, null, null, null, running, ex.Message);
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

    private static bool ContainsValue(string? current, string expected) =>
        (current ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(expected.Trim(), StringComparer.OrdinalIgnoreCase);

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

    private static IEnumerable<string> AccessManagerProcessNames()
    {
        yield return "Access Manager";
        yield return "NDI Access Manager";
    }
}
