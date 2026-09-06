using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NDIJobConfigurator.Core;

public sealed class WindowsPcAgentService(
    AppStateStore store,
    IHttpClientFactory clients,
    ILogger<WindowsPcAgentService> logger) : BackgroundService
{
    public const string ProductName = WindowsPcAgentContract.ProductName;
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
            catch (Exception ex) { logger.LogWarning(ex, "NDI Configurator PC Agent discovery pass failed"); }
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
                        DateTimeOffset.UtcNow,
                        status.NetworkConfiguration,
                        status.MulticastConfiguration);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    logger.LogDebug("NDI Configurator PC Agent validation timed out for {Address}", reply.Address);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
                {
                    logger.LogDebug(ex, "NDI Configurator PC Agent validation failed for {Address}", reply.Address);
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
            catch (SocketException ex) { logger.LogDebug(ex, "Ignoring malformed NDI Configurator PC Agent discovery reply"); }
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
        if (healthPayload is null || healthPayload.SchemaVersion != 1 || !WindowsPcAgentContract.IsCompatibleProduct(healthPayload.Product) || healthPayload.Status != "ok") return null;
        using var response = await client.GetAsync($"http://{address}:{apiPort}/api/v1/status", ct);
        if (!response.IsSuccessStatusCode) return null;
        var status = await response.Content.ReadFromJsonAsync<WindowsPcAgentStatus>(AgentJson.Options, ct);
        return status is not null && status.SchemaVersion == 1 && WindowsPcAgentContract.IsCompatibleProduct(status.Product)
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
            ?? throw new KeyNotFoundException($"The {ProductName} is no longer discoverable on the selected subnet.");
        if (!agent.Capabilities.Contains("remote-onboarding-v2", StringComparer.Ordinal)
            || !agent.Capabilities.Contains("network-config-v1", StringComparer.Ordinal))
            throw new NotSupportedException($"{ProductName} update required for managed remote onboarding.");
        if (!IsSelectedSubnetAddress(agent.Address, network)) throw new InvalidOperationException($"The {ProductName} is outside the selected subnet.");
        if (state.LastJob is null) throw new InvalidOperationException("Create or open a job before requesting PC onboarding.");
        var serverAddress = network.Address;
        using var client = CreateBoundClient(network, TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://{agent.Address}:{agent.ApiPort}/api/v1/onboarding/open")
        {
            Content = JsonContent.Create(
                new WindowsPcAgentOpenRequest(
                    Environment.MachineName,
                    serverAddress,
                    state.LastJob.JobName,
                    $"http://{serverAddress}:8091/"),
                options: AgentJson.Options)
        };
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        return response.StatusCode;
    }

    public async Task<WindowsPcAgentMulticastResult> ConfigureMulticastAsync(
        string endpointId,
        string mode,
        string jobName,
        string? netPrefix,
        string? netmask,
        int? ttl,
        CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var network = NetworkAddressing.ResolveLocalInterface(state.SelectedNetworkAdapterId, state.SelectedNetworkAddress)
            ?? throw new InvalidOperationException("The selected onboarding adapter is no longer active.");
        var agent = Snapshot().FirstOrDefault(candidate =>
            string.Equals(candidate.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"The {ProductName} is no longer discoverable on the selected subnet.");
        if (!agent.Capabilities.Contains("multicast-config-v1", StringComparer.Ordinal))
            throw new NotSupportedException($"Update the {ProductName} to configure NDI Access Manager multicast remotely.");
        if (!IsSelectedSubnetAddress(agent.Address, network))
            throw new InvalidOperationException($"The {ProductName} is outside the selected subnet.");
        if (state.LastJob is null || !string.Equals(state.LastJob.JobName, jobName, StringComparison.Ordinal))
            throw new InvalidOperationException("The active job no longer matches the multicast request.");

        var multicast = string.Equals(mode, "multicast", StringComparison.Ordinal);
        var payload = new WindowsPcAgentMulticastRequest(
            1,
            endpointId,
            jobName,
            agent.AdapterId,
            multicast ? "multicast" : "unicast",
            multicast,
            multicast,
            multicast ? netPrefix : null,
            multicast ? netmask : null,
            multicast ? ttl : null);
        logger.LogInformation(
            "Sending remote NDI multicast {Mode} request for endpoint {EndpointId}, job {JobName}, adapter {AdapterId}, prefix {NetPrefix}, mask {Netmask}, TTL {Ttl}",
            payload.Mode,
            endpointId,
            jobName,
            agent.AdapterId,
            payload.NetPrefix,
            payload.Netmask,
            payload.Ttl);
        using var client = CreateBoundClient(network, TimeSpan.FromSeconds(15));
        using var response = await client.PutAsJsonAsync(
            $"http://{agent.Address}:{agent.ApiPort}/api/v1/multicast/configuration",
            payload,
            AgentJson.Options,
            ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"{ProductName} returned HTTP {(int)response.StatusCode} while applying multicast settings."
                : $"{ProductName} returned HTTP {(int)response.StatusCode}: {detail}");
        }
        var result = await response.Content.ReadFromJsonAsync<WindowsPcAgentMulticastResult>(AgentJson.Options, ct)
            ?? throw new JsonException($"{ProductName} returned an empty multicast result.");
        ValidateMulticastResult(result, payload);
        logger.LogInformation(
            "Remote NDI multicast {Mode} verified for endpoint {EndpointId}; send {SendEnabled}, receive {ReceiveEnabled}, in use {InUse}",
            result.Mode,
            endpointId,
            result.SendEnabled,
            result.ReceiveEnabled,
            result.InUse);
        return result;
    }

    private static void ValidateMulticastResult(
        WindowsPcAgentMulticastResult result,
        WindowsPcAgentMulticastRequest request)
    {
        if (result.SchemaVersion != 1
            || !WindowsPcAgentContract.IsCompatibleProduct(result.Product)
            || !string.Equals(result.EndpointId, request.EndpointId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.AdapterId, request.AdapterId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.Mode, request.Mode, StringComparison.Ordinal)
            || result.SendEnabled != request.SendEnabled
            || result.ReceiveEnabled != request.ReceiveEnabled
            || !result.InUse
            || !string.Equals(result.NetPrefix, request.NetPrefix, StringComparison.Ordinal)
            || !string.Equals(result.Netmask, request.Netmask, StringComparison.Ordinal)
            || result.Ttl != request.Ttl
            || (request.Mode == "multicast" && !string.Equals(result.JobName, request.JobName, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{ProductName} did not verify the requested NDI Access Manager multicast state.");
    }

    private static HttpClient CreateBoundClient(LocalNetworkInterface network, TimeSpan timeout)
    {
        var localAddress = IPAddress.Parse(network.Address);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            UseProxy = false,
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(localAddress, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    private static bool Compatible(WindowsPcAgentDiscovery? reply) => reply is
    {
        SchemaVersion: 1,
        ApiPort: DefaultApiPort,
        Status: "online"
    } && WindowsPcAgentContract.IsCompatibleProduct(reply.Product)
      && Guid.TryParse(reply.EndpointId, out _)
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
