using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace KiloviewSetup.Core;

public sealed class WindowsPcAgentService(
    AppStateStore store,
    IHttpClientFactory clients,
    ILogger<WindowsPcAgentService> logger) : BackgroundService
{
    public const int DiscoveryPort = 8093;
    public const int DefaultApiPort = 8094;
    private const string Probe = "KILOVIEW_PC_AGENT_DISCOVER_V1";
    private readonly ConcurrentDictionary<string, WindowsPcAgentSnapshot> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);

    public IReadOnlyList<WindowsPcAgentSnapshot> Snapshot() => _agents.Values
        .OrderBy(agent => NetworkAddressing.ToUInt(IPAddress.Parse(agent.Address)))
        .ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DiscoverAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Windows PC Agent discovery pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    public async Task DiscoverAsync(CancellationToken ct)
    {
        await _discoveryGate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync();
            var network = NetworkAddressing.ResolveLocalInterface(state.SelectedNetworkAdapterId, state.SelectedNetworkAddress);
            if (network is null)
            {
                _agents.Clear();
                return;
            }

            var networkAddress = NetworkAddressing.FromUInt(
                NetworkAddressing.ToUInt(IPAddress.Parse(network.Address)) &
                (uint.MaxValue << (32 - network.PrefixLength)));
            var candidates = NetworkAddressing.ExpandCidr(NetworkAddressing.GetScanCidr(network))
                .Where(address => !string.Equals(address.ToString(), network.Address, StringComparison.Ordinal))
                .Take(8192)
                .ToArray();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(network.Address), 0));
            var payload = Encoding.UTF8.GetBytes(Probe);
            var receiveUntil = DateTimeOffset.UtcNow.AddSeconds(4);
            var replies = new ConcurrentDictionary<string, WindowsPcAgentDiscovery>(StringComparer.OrdinalIgnoreCase);
            var receiver = ReceiveRepliesAsync(udp, network, networkAddress, replies, () => receiveUntil, ct);

            foreach (var batch in candidates.Chunk(64))
            {
                foreach (var address in batch)
                    await udp.SendAsync(payload, new IPEndPoint(address, DiscoveryPort), ct);
                await Task.Delay(20, ct);
            }
            receiveUntil = DateTimeOffset.UtcNow.AddMilliseconds(1500);
            await receiver;

            var validated = new ConcurrentDictionary<string, WindowsPcAgentSnapshot>(StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(replies.Values, new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct }, async (reply, token) =>
            {
                try
                {
                    var status = await TryReadStatusAsync(reply.Address, reply.ApiPort, reply.EndpointId, network, token);
                    if (status is null) return;
                    var memberships = reply.Capabilities.Contains("memberships-v1", StringComparer.Ordinal)
                        ? await TryReadMembershipsAsync(reply.Address, reply.ApiPort, reply.EndpointId, network, token) ?? status.Memberships
                        : status.Memberships;
                    memberships ??= [];
                    validated[reply.EndpointId] = new WindowsPcAgentSnapshot(
                status.EndpointId,
                status.Hostname,
                status.Address,
                status.PrefixLength,
                reply.ApiPort,
                status.AgentVersion,
                reply.Capabilities,
                status.Status,
                status.OperatingSystemVersion,
                status.AdapterId,
                status.AdapterName,
                status.NdiToolsInstalled,
                status.NdiToolsVersion,
                status.AgentStartedUtc,
                status.AgentUptimeSeconds,
                status.MachineUptimeSeconds,
                status.PhysicalMemoryTotalBytes,
                status.PhysicalMemoryAvailableBytes,
                status.SystemDriveTotalBytes,
                status.SystemDriveFreeBytes,
                memberships,
                status.ObservedUtc,
                        DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    logger.LogDebug("PC Agent validation timed out for {Address}", reply.Address);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
                {
                    logger.LogDebug(ex, "PC Agent validation failed for {Address}", reply.Address);
                }
            });

            foreach (var agent in validated) _agents[agent.Key] = agent.Value;
            foreach (var stale in _agents.Where(item => !validated.ContainsKey(item.Key)).Select(item => item.Key).ToArray())
                _agents.TryRemove(stale, out _);
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task ReceiveRepliesAsync(
        UdpClient udp,
        LocalNetworkInterface network,
        IPAddress networkAddress,
        ConcurrentDictionary<string, WindowsPcAgentDiscovery> replies,
        Func<DateTimeOffset> deadline,
        CancellationToken ct)
    {
        while (DateTimeOffset.UtcNow < deadline())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
            try
            {
                var result = await udp.ReceiveAsync(timeout.Token);
                var source = result.RemoteEndPoint.Address;
                if (!NetworkAddressing.Contains(source, networkAddress, network.PrefixLength)) continue;
                var reply = JsonSerializer.Deserialize<WindowsPcAgentDiscovery>(result.Buffer, AgentJson.Options);
                if (!Compatible(reply) || !string.Equals(reply!.Address, source.ToString(), StringComparison.Ordinal)) continue;
                replies[reply.EndpointId] = reply;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (JsonException) { }
            catch (SocketException ex) { logger.LogDebug(ex, "Ignoring malformed PC Agent discovery reply"); }
        }
    }

    public async Task<WindowsPcAgentStatus?> TryReadStatusAsync(
        string address,
        int apiPort,
        string endpointId,
        LocalNetworkInterface network,
        CancellationToken ct)
    {
        if (!IsSelectedSubnetAddress(address, network) || apiPort != DefaultApiPort || !Guid.TryParse(endpointId, out _)) return null;
        var client = clients.CreateClient("WindowsPcAgent");
        using var health = await client.GetAsync($"http://{address}:{apiPort}/api/health", ct);
        if (!health.IsSuccessStatusCode) return null;
        var healthPayload = await health.Content.ReadFromJsonAsync<WindowsPcAgentHealth>(AgentJson.Options, ct);
        if (healthPayload is null || healthPayload.SchemaVersion != 1 || healthPayload.Product != "Kiloview PC Agent" || healthPayload.Status != "ok") return null;
        using var response = await client.GetAsync($"http://{address}:{apiPort}/api/v1/status", ct);
        if (!response.IsSuccessStatusCode) return null;
        var status = await response.Content.ReadFromJsonAsync<WindowsPcAgentStatus>(AgentJson.Options, ct);
        return status is not null && status.SchemaVersion == 1 && status.Product == "Kiloview PC Agent"
            && status.Status == "online"
            && string.Equals(status.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(status.Address, address, StringComparison.Ordinal)
            ? status
            : null;
    }

    public async Task<IReadOnlyList<WindowsPcAgentMembership>?> TryReadMembershipsAsync(
        string address,
        int apiPort,
        string endpointId,
        LocalNetworkInterface network,
        CancellationToken ct)
    {
        if (!IsSelectedSubnetAddress(address, network) || apiPort != DefaultApiPort || !Guid.TryParse(endpointId, out _)) return null;
        var client = clients.CreateClient("WindowsPcAgent");
        using var response = await client.GetAsync($"http://{address}:{apiPort}/api/v1/memberships", ct);
        if (!response.IsSuccessStatusCode) return null;
        var payload = await response.Content.ReadFromJsonAsync<WindowsPcAgentMemberships>(AgentJson.Options, ct);
        return payload is not null && string.Equals(payload.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)
            ? payload.Memberships ?? []
            : null;
    }

    public async Task<HttpStatusCode> OpenOnboardingAsync(string endpointId, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var network = NetworkAddressing.ResolveLocalInterface(state.SelectedNetworkAdapterId, state.SelectedNetworkAddress)
            ?? throw new InvalidOperationException("The selected onboarding adapter is no longer active.");
        var agent = Snapshot().FirstOrDefault(candidate => string.Equals(candidate.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The PC Agent is no longer discoverable on the selected subnet.");
        if (!agent.Capabilities.Contains("open-onboarding-v1", StringComparer.Ordinal))
            throw new NotSupportedException("This PC Agent does not advertise locally approved onboarding.");
        if (!IsSelectedSubnetAddress(agent.Address, network)) throw new InvalidOperationException("The PC Agent is outside the selected subnet.");
        if (state.LastJob is null) throw new InvalidOperationException("Create or open a job before requesting PC onboarding.");
        var serverAddress = network.Address;
        var client = clients.CreateClient("WindowsPcAgentOnboarding");
        using var response = await client.PostAsJsonAsync(
            $"http://{agent.Address}:{agent.ApiPort}/api/v1/onboarding/open",
            new WindowsPcAgentOpenRequest(Environment.MachineName, serverAddress, state.LastJob.JobName, $"http://{serverAddress}:8091/"),
            AgentJson.Options,
            ct);
        return response.StatusCode;
    }

    private static bool Compatible(WindowsPcAgentDiscovery? reply) => reply is
    {
        SchemaVersion: 1,
        Product: "Kiloview PC Agent",
        ApiPort: DefaultApiPort,
        Status: "online"
    } && Guid.TryParse(reply.EndpointId, out _)
      && !string.IsNullOrWhiteSpace(reply.AgentVersion)
      && IPAddress.TryParse(reply.Address, out var address) && address.AddressFamily == AddressFamily.InterNetwork
      && reply.PrefixLength is >= 1 and <= 30
      && reply.Capabilities is not null
      && reply.Capabilities.Contains("status-v1", StringComparer.Ordinal);

    private static bool IsSelectedSubnetAddress(string address, LocalNetworkInterface network) =>
        IPAddress.TryParse(address, out var candidate) &&
        NetworkAddressing.Contains(candidate, IPAddress.Parse(network.Address), network.PrefixLength);
}

internal static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
