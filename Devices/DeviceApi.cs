using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

public sealed class DeviceApiException(string message, Exception? inner = null) : Exception(message, inner);

public interface IDeviceApi
{
    Task<ManagedDevice> ReadAsync(CancellationToken ct);
    Task ProvisionAccessAsync(DeviceCredentials targetCredentials, CancellationToken ct);
    Task SetNetworkAsync(string address, string mask, string gateway, CancellationToken ct);
    Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct);
    Task SetRoleAsync(DeviceRole role, CancellationToken ct);
    Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct);
    Task ShowIdentityAsync(TitleCardSource source, CancellationToken ct);
    Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct);
    Task BlankAsync(CancellationToken ct);
}

internal abstract class HttpDeviceApi(string ipAddress, DeviceCredentials credentials)
{
    protected string IpAddress { get; } = ipAddress;
    protected DeviceCredentials Credentials { get; } = credentials;
    protected CookieContainer Cookies { get; } = new();

    protected HttpClient NewClient(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = Cookies,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        return new HttpClient(handler) { BaseAddress = new Uri($"http://{IpAddress}"), Timeout = timeout ?? TimeSpan.FromSeconds(6) };
    }

    protected static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new DeviceApiException($"{operation} failed with HTTP {(int)response.StatusCode}.");
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.String &&
                !string.Equals(result.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                var message = doc.RootElement.TryGetProperty("msg", out var msg) ? msg.ToString() : result.GetString();
                doc.Dispose();
                throw new DeviceApiException($"{operation} was rejected by the device: {message}");
            }
            return doc;
        }
        catch (JsonException ex) { throw new DeviceApiException($"{operation} returned an invalid response.", ex); }
    }

    protected async Task<JsonDocument> GetAsync(HttpClient client, string path, string operation, CancellationToken ct) =>
        await ReadJsonAsync(await client.GetAsync(path, ct), operation, ct);

    protected async Task<JsonDocument> PostAsync(HttpClient client, string path, object? body, string operation, CancellationToken ct) =>
        await ReadJsonAsync(await client.PostAsJsonAsync(path, body ?? new { }, ct), operation, ct);

    protected async Task<bool> TryMutationAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        try { using var response = await PostAsync(client, path, body, "apply device first-login setting", ct); return true; }
        catch (DeviceApiException) { }
        try
        {
            using var response = await client.PostAsync(path, null, ct);
            if (await MutationSucceededAsync(response, ct)) return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        try
        {
            var separator = path.Contains('?') ? '&' : '?';
            using var response = await client.GetAsync($"{path}{separator}accept=true&accepted=true&agree=true", ct);
            if (await MutationSucceededAsync(response, ct)) return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        return false;
    }

    private static async Task<bool> MutationSucceededAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true) return false;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return true;
        try
        {
            using var body = JsonDocument.Parse(text);
            return !body.RootElement.TryGetProperty("result", out var result) || !string.Equals(result.GetString(), "error", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    protected async Task<IReadOnlyList<string>> FindFirstLoginApiPathsAsync(HttpClient client, CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assets = new Queue<string>();
        assets.Enqueue("/");
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (assets.Count > 0 && visited.Count < 36)
        {
            var asset = assets.Dequeue();
            if (!visited.Add(asset)) continue;
            string text;
            try
            {
                using var response = await client.GetAsync(asset, ct);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 8_000_000) continue;
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { continue; }

            foreach (Match match in Regex.Matches(text, "(?:src=|[\\\"'])(?<asset>/?(?:static/)?(?:js/)?[A-Za-z0-9_.~/-]+\\.js(?:\\?[^\\\"'<> ]*)?)", RegexOptions.IgnoreCase))
            {
                var candidate = match.Groups["asset"].Value;
                if (!candidate.StartsWith('/')) candidate = "/" + candidate;
                if (!visited.Contains(candidate)) assets.Enqueue(candidate);
            }
            foreach (Match match in Regex.Matches(text, "[\\\"'](?<api>/?api/[A-Za-z0-9_./?-]+)[\\\"']", RegexOptions.IgnoreCase))
            {
                var candidate = match.Groups["api"].Value;
                if (!candidate.Contains("eula", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.Contains("license", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.Contains("agreement", StringComparison.OrdinalIgnoreCase)) continue;
                if (!candidate.StartsWith('/')) candidate = "/" + candidate;
                paths.Add(candidate);
            }
        }
        return paths.ToArray();
    }

    protected static string String(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) ? value.ToString() : fallback;
}
