using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

public sealed class DeviceApiException(string message, Exception? inner = null) : Exception(message, inner);

public interface IDeviceApi
{
    Task<ManagedDevice> ReadAsync(CancellationToken ct);
    Task SetNetworkAsync(string address, string mask, string gateway, CancellationToken ct);
    Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct);
    Task SetRoleAsync(DeviceRole role, CancellationToken ct);
    Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct);
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

    protected static string String(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) ? value.ToString() : fallback;
}
