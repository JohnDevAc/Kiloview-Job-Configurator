using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NDIJobConfigurator.Core;

public sealed class WindowsPcRemoteOnboardingService(
    AppStateStore store,
    WindowsPcAgentService agents,
    ILogger<WindowsPcRemoteOnboardingService> logger)
{
    private static readonly TimeSpan ConfigurationLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PendingConfiguration> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RecentResult> _results = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WindowsPcRemoteOnboardingState> StageAsync(
        string endpointId,
        WindowsPcRemoteOnboardingRequest request,
        string requestedBy)
    {
        Cleanup();
        if (!Guid.TryParse(endpointId, out _))
            throw new ArgumentException("The Windows PC endpoint identifier is invalid.");
        var state = await store.ReadAsync();
        var network = NetworkAddressing.ResolveLocalInterface(
            state.SelectedNetworkAdapterId,
            state.SelectedNetworkAddress)
            ?? throw new InvalidOperationException("The selected onboarding adapter is no longer active.");
        if (!AllowedOperator(requestedBy, network))
            throw new UnauthorizedAccessException("Remote onboarding can only be staged from the selected production subnet.");
        var job = state.LastJob
            ?? throw new InvalidOperationException("Create or open a job before requesting PC onboarding.");
        var agent = agents.Snapshot().FirstOrDefault(candidate =>
            string.Equals(candidate.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("The NDI Configurator PC Agent is no longer discoverable on the selected subnet.");
        RequireRemoteCapabilities(agent);
        if (string.Equals(agent.Address, network.Address, StringComparison.Ordinal))
            throw new InvalidOperationException("Use server PC onboarding for this local endpoint. Its network address is retained.");
        if (!Contains(network, agent.Address))
            throw new UnauthorizedAccessException("The NDI Configurator PC Agent is outside the selected production subnet.");

        var desired = ValidateNetwork(request.Network, agent, network);
        var now = DateTimeOffset.UtcNow;
        var pending = new PendingConfiguration(
            endpointId,
            agent.Address,
            requestedBy,
            job.JobName,
            job.NdiDiscoveryServerIp,
            desired,
            now,
            now.Add(ConfigurationLifetime),
            "staged",
            "Configuration staged; waiting for local approval.",
            null,
            null);
        _pending[endpointId] = pending;
        _results.TryRemove(endpointId, out _);
        logger.LogInformation(
            "Remote Windows onboarding staged for endpoint {EndpointId} by {RequestedBy}; adapter {AdapterId}; before DHCP {BeforeDhcp}, address {BeforeAddress}/{BeforePrefix}, gateways {BeforeGateways}, DNS {BeforeDns}; after mode {Mode}, address {AfterAddress}/{AfterPrefix}, gateway {AfterGateway}, DNS {AfterDns}; expires {ExpiresUtc}",
            endpointId,
            requestedBy,
            agent.AdapterId,
            agent.NetworkConfiguration?.DhcpEnabled,
            agent.Address,
            agent.PrefixLength,
            Join(agent.NetworkConfiguration?.DefaultGateways),
            Join(agent.NetworkConfiguration?.DnsServers),
            desired.Mode,
            desired.Address,
            desired.PrefixLength,
            desired.DefaultGateway,
            desired.DnsServers is null ? "retain" : Join(desired.DnsServers),
            pending.ExpiresUtc);
        return ToState(pending);
    }

    public async Task<WindowsPcRemoteOnboardingConfiguration> GetConfigurationAsync(
        string endpointId,
        string requestAddress)
    {
        var started = Stopwatch.GetTimestamp();
        logger.LogInformation(
            "Remote Windows onboarding configuration fetch started for endpoint {EndpointId} from {RequestAddress}",
            endpointId,
            requestAddress);
        Cleanup();
        try
        {
            if (!_pending.TryGetValue(endpointId, out var pending))
                throw new KeyNotFoundException("No pending remote onboarding configuration exists for this endpoint.");
            var state = await store.ReadAsync();
            var network = NetworkAddressing.ResolveLocalInterface(
                state.SelectedNetworkAdapterId,
                state.SelectedNetworkAddress)
                ?? throw new InvalidOperationException("The selected onboarding adapter is no longer active.");
            if (!string.Equals(requestAddress, pending.AgentAddress, StringComparison.Ordinal)
                || !Contains(network, requestAddress))
                throw new UnauthorizedAccessException("This client is not allowed to fetch the pending endpoint configuration.");
            if (state.LastJob is null
                || !string.Equals(state.LastJob.JobName, pending.JobName, StringComparison.Ordinal))
                throw new InvalidOperationException("The active job no longer matches the pending endpoint configuration.");

            var fetchedUtc = DateTimeOffset.UtcNow;
            pending = pending with { ConfigurationFetchedUtc = fetchedUtc };
            _pending[endpointId] = pending;
            logger.LogInformation(
                "Remote Windows onboarding configuration fetch completed for endpoint {EndpointId} from {RequestAddress} in {ElapsedMilliseconds} ms",
                endpointId,
                requestAddress,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new(
                1,
                "NDI Job Configurator",
                pending.EndpointId,
                pending.JobName,
                pending.NdiDiscoveryServerIp,
                pending.Network);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Remote Windows onboarding configuration fetch failed for endpoint {EndpointId} from {RequestAddress} after {ElapsedMilliseconds} ms",
                endpointId,
                requestAddress,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    public void RecordApprovalRequestStarted(string endpointId)
    {
        if (!_pending.TryGetValue(endpointId, out var pending)) return;
        _pending[endpointId] = pending with
        {
            Status = "awaiting-local-approval",
            Message = "Waiting for the endpoint user to approve onboarding locally.",
            RegistrationDeadlineUtc = null
        };
    }

    public WindowsPcRemoteOnboardingState? Status(string endpointId)
    {
        Cleanup();
        if (_pending.TryGetValue(endpointId, out var pending))
        {
            if (pending.RegistrationDeadlineUtc is { } deadline && DateTimeOffset.UtcNow > deadline)
            {
                pending = pending with
                {
                    Status = "failed",
                    Message = pending.ConfigurationFetchedUtc is null
                        ? "The PC approved onboarding but never fetched its staged configuration and did not register within 90 seconds. Check LAN/firewall access to TCP 8091, then retry."
                        : "The PC fetched its staged configuration but did not register within 90 seconds. Check the endpoint's elevated onboarding result, then retry."
                };
                _pending[endpointId] = pending;
            }
            return ToState(pending);
        }
        return _results.TryGetValue(endpointId, out var result) ? result.State : null;
    }

    public void RecordAgentResponse(string endpointId, HttpStatusCode statusCode)
    {
        if (!_pending.TryGetValue(endpointId, out var pending)) return;
        var now = DateTimeOffset.UtcNow;
        pending = statusCode switch
        {
            HttpStatusCode.Accepted => pending with
            {
                Status = "awaiting-registration",
                Message = "The endpoint user approved onboarding. Waiting for the elevated utility to apply settings and register.",
                RegistrationDeadlineUtc = now.Add(RegistrationTimeout)
            },
            HttpStatusCode.Forbidden => pending with
            {
                Status = "denied",
                Message = "The endpoint user denied onboarding. The staged configuration remains available until it expires.",
                RegistrationDeadlineUtc = null
            },
            _ => pending with
            {
                Status = "failed",
                Message = $"The NDI Configurator PC Agent returned HTTP {(int)statusCode}. The staged configuration remains available for a retry.",
                RegistrationDeadlineUtc = null
            }
        };
        _pending[endpointId] = pending;
        logger.LogInformation(
            "Remote Windows onboarding agent response for endpoint {EndpointId}: HTTP {StatusCode}; state {State}",
            endpointId,
            (int)statusCode,
            pending.Status);
    }

    public void RecordFailure(string endpointId, string message)
    {
        if (!_pending.TryGetValue(endpointId, out var pending)) return;
        _pending[endpointId] = pending with
        {
            Status = "failed",
            Message = message,
            RegistrationDeadlineUtc = null
        };
        logger.LogWarning("Remote Windows onboarding failed for endpoint {EndpointId}: {Message}", endpointId, message);
    }

    public void RecordRegistration(WindowsPcEndpoint endpoint)
    {
        if (!_pending.TryRemove(endpoint.EndpointId, out var pending)) return;
        var now = DateTimeOffset.UtcNow;
        var state = new WindowsPcRemoteOnboardingState(
            endpoint.EndpointId,
            "completed",
            $"Onboarding completed and the PC registered at {endpoint.Address}.",
            pending.RequestedUtc,
            pending.RegistrationDeadlineUtc,
            now.Add(ResultLifetime),
            endpoint.Address,
            pending.ConfigurationFetchedUtc);
        _results[endpoint.EndpointId] = new(state, now.Add(ResultLifetime));
        logger.LogInformation(
            "Remote Windows onboarding registration completed for endpoint {EndpointId}; previous address {BeforeAddress}; registered address {AfterAddress}; requested {RequestedUtc}; registered {RegisteredUtc}",
            endpoint.EndpointId,
            pending.AgentAddress,
            endpoint.Address,
            pending.RequestedUtc,
            now);
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _pending.Where(item => item.Value.ExpiresUtc <= now).ToArray())
        {
            if (!_pending.TryRemove(item.Key, out var expired)) continue;
            var state = new WindowsPcRemoteOnboardingState(
                expired.EndpointId,
                "expired",
                "The pending remote onboarding configuration expired before registration completed.",
                expired.RequestedUtc,
                expired.RegistrationDeadlineUtc,
                now.Add(ResultLifetime),
                ConfigurationFetchedUtc: expired.ConfigurationFetchedUtc);
            _results[item.Key] = new(state, now.Add(ResultLifetime));
            logger.LogWarning("Remote Windows onboarding configuration expired for endpoint {EndpointId}", item.Key);
        }
        foreach (var item in _results.Where(item => item.Value.ExpiresUtc <= now).ToArray())
            _results.TryRemove(item.Key, out _);
    }

    private static WindowsPcRemoteNetworkConfiguration ValidateNetwork(
        WindowsPcRemoteNetworkRequest? request,
        WindowsPcAgentSnapshot agent,
        LocalNetworkInterface selectedNetwork)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Mode))
            throw new ArgumentException("Choose unchanged, DHCP, or static network configuration.");
        if (string.IsNullOrWhiteSpace(request.AdapterId)
            || !string.Equals(request.AdapterId.Trim(), agent.AdapterId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose the adapter currently selected by the NDI Configurator PC Agent.");
        var mode = request.Mode.Trim().ToLowerInvariant();
        if (mode is "unchanged" or "dhcp")
        {
            if (!string.IsNullOrWhiteSpace(request.Address)
                || request.PrefixLength is not null
                || !string.IsNullOrWhiteSpace(request.DefaultGateway)
                || request.DnsServers is not null)
                throw new ArgumentException($"{mode} mode must not include static IPv4, gateway, or DNS fields.");
            return new(agent.AdapterId, mode);
        }
        if (mode != "static")
            throw new ArgumentException("The network mode must be unchanged, dhcp, or static.");
        var address = ParseIpv4(request.Address, "Static IPv4 address");
        if (request.PrefixLength is not int prefix || prefix is < 1 or > 30)
            throw new ArgumentException("Static prefix length must be from 1 to 30.");
        EnsureHost(address, prefix, "Static IPv4 address");
        EnsureUnicast(address, "Static IPv4 address");
        var server = IPAddress.Parse(selectedNetwork.Address);
        if (!NetworkAddressing.Contains(address, server, selectedNetwork.PrefixLength)
            || !NetworkAddressing.Contains(server, address, prefix))
            throw new ArgumentException("The static IPv4 address must remain in the requesting Configurator's selected subnet.");
        if (address.Equals(server))
            throw new ArgumentException("The static IPv4 address cannot be the Job Configurator's own address.");

        string? gateway = null;
        if (!string.IsNullOrWhiteSpace(request.DefaultGateway))
        {
            var parsed = ParseIpv4(request.DefaultGateway, "Default gateway");
            EnsureHost(parsed, prefix, "Default gateway");
            EnsureUnicast(parsed, "Default gateway");
            if (!NetworkAddressing.Contains(parsed, address, prefix))
                throw new ArgumentException("The default gateway must be inside the configured static subnet.");
            if (parsed.Equals(address))
                throw new ArgumentException("The default gateway cannot be the PC's static IPv4 address.");
            gateway = parsed.ToString();
        }

        IReadOnlyList<string>? dns = null;
        if (request.DnsServers is not null)
        {
            if (request.DnsServers.Count > 4)
                throw new ArgumentException("Specify no more than four DNS servers.");
            dns = request.DnsServers.Select(value =>
            {
                var parsed = ParseIpv4(value, "DNS server");
                EnsureUnicast(parsed, $"DNS server '{value}'");
                return parsed.ToString();
            }).Distinct(StringComparer.Ordinal).ToArray();
        }
        return new(agent.AdapterId, mode, address.ToString(), prefix, gateway, dns);
    }

    private static void RequireRemoteCapabilities(WindowsPcAgentSnapshot agent)
    {
        if (!agent.Capabilities.Contains("remote-onboarding-v2", StringComparer.Ordinal)
            || !agent.Capabilities.Contains("network-config-v1", StringComparer.Ordinal))
            throw new NotSupportedException("NDI Configurator PC Agent update required for managed remote onboarding.");
    }

    private static IPAddress ParseIpv4(string? value, string field)
    {
        if (!IPAddress.TryParse(value, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException($"{field} must be valid IPv4.");
        return address;
    }

    private static void EnsureHost(IPAddress address, int prefix, string field)
    {
        var value = NetworkAddressing.ToUInt(address);
        var mask = uint.MaxValue << (32 - prefix);
        var network = value & mask;
        var broadcast = network | ~mask;
        if (value == network || value == broadcast)
            throw new ArgumentException($"{field} cannot be a subnet or broadcast address.");
    }

    private static void EnsureUnicast(IPAddress address, string field)
    {
        var first = address.GetAddressBytes()[0];
        if (address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.Broadcast)
            || IPAddress.IsLoopback(address)
            || first == 0
            || first >= 224)
            throw new ArgumentException($"{field} is not a usable unicast IPv4 address.");
    }

    private static bool AllowedOperator(string address, LocalNetworkInterface network) =>
        IPAddress.TryParse(address, out var parsed)
        && (IPAddress.IsLoopback(parsed) || NetworkAddressing.Contains(parsed, IPAddress.Parse(network.Address), network.PrefixLength));

    private static bool Contains(LocalNetworkInterface network, string address) =>
        IPAddress.TryParse(address, out var parsed)
        && NetworkAddressing.Contains(parsed, IPAddress.Parse(network.Address), network.PrefixLength);

    private static string Join(IReadOnlyList<string>? values) =>
        values is null ? "unknown" : values.Count == 0 ? "none" : string.Join(',', values);

    private static WindowsPcRemoteOnboardingState ToState(PendingConfiguration pending) => new(
        pending.EndpointId,
        pending.Status,
        pending.Message,
        pending.RequestedUtc,
        pending.RegistrationDeadlineUtc,
        pending.ExpiresUtc,
        ConfigurationFetchedUtc: pending.ConfigurationFetchedUtc);

    private sealed record PendingConfiguration(
        string EndpointId,
        string AgentAddress,
        string RequestedBy,
        string JobName,
        string NdiDiscoveryServerIp,
        WindowsPcRemoteNetworkConfiguration Network,
        DateTimeOffset RequestedUtc,
        DateTimeOffset ExpiresUtc,
        string Status,
        string Message,
        DateTimeOffset? RegistrationDeadlineUtc,
        DateTimeOffset? ConfigurationFetchedUtc);

    private sealed record RecentResult(WindowsPcRemoteOnboardingState State, DateTimeOffset ExpiresUtc);
}
