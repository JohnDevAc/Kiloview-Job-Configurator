using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NDIJobConfigurator.Core;

public sealed class WindowsPcRemoteOnboardingService(
    AppStateStore store,
    WindowsPcAgentService agents,
    ILogger<WindowsPcRemoteOnboardingService> logger,
    TimeProvider? timeProvider = null,
    OnboardingDiagnosticsStore? diagnostics = null)
{
    private static readonly TimeSpan ConfigurationLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromMinutes(5);
    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PendingConfiguration> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RecentResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _stageGate = new(1, 1);

    public async Task<WindowsPcRemoteOnboardingState> StageAsync(
        string endpointId,
        WindowsPcRemoteOnboardingRequest request,
        string requestedBy)
    {
        await _stageGate.WaitAsync();
        try
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
            if (Status(endpointId, state) is { Status: "staged" or "awaiting-local-approval" or "awaiting-registration" })
                throw new InvalidOperationException("An onboarding attempt is already active for this PC. Wait for completion or expiry before retrying.");
            if (desired.Mode == "static" && desired.Address is { } target
                && (KnownInfrastructure(state, agent, desired).Contains(target)
                    || state.Devices.Any(d => d.IpAddress == target)
                    || (state.WindowsPcs ?? []).Any(pc => pc.EndpointId != endpointId && pc.Address == target)
                    || agents.Snapshot().Any(pc => pc.EndpointId != endpointId && pc.Address == target)
                    || (state.PcOnboardingReceipts ?? []).Any(receipt => receipt.EndpointId != endpointId
                        && receipt.Status is "applying" or "registered" or "recovery-required"
                        && receipt.Network.Address == target)
                    || _pending.Values.Any(pc => pc.EndpointId != endpointId && pc.Network.Address == target && IsActive(pc))))
                throw new InvalidOperationException("The requested static address is occupied or reserved by job infrastructure or another device/PC.");
            var now = Now;
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
                null,
                Guid.NewGuid().ToString("D"), state.JobId!, state.JobRevision!);
            diagnostics?.Register(new(endpointId, pending.AttemptId, agent.Hostname, state.JobId, state.JobRevision,
                job.JobName, agent.Address, desired.Mode == "static" ? desired.Address : null, agent.AdapterId,
                false, now, now, []));
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
        finally { _stageGate.Release(); }
    }

    public async Task<WindowsPcRemoteOnboardingConfiguration> GetConfigurationAsync(
        string endpointId,
        string requestAddress,
        string? attemptId = null)
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
            if (!string.Equals(attemptId, pending.AttemptId, StringComparison.Ordinal) || !IsActive(pending))
                throw new InvalidOperationException("This onboarding attempt is missing or has been replaced. Request fresh local approval.");
            var state = await store.ReadAsync();
            var network = NetworkAddressing.ResolveLocalInterface(
                state.SelectedNetworkAdapterId,
                state.SelectedNetworkAddress)
                ?? throw new InvalidOperationException("The selected onboarding adapter is no longer active.");
            if (!string.Equals(requestAddress, pending.AgentAddress, StringComparison.Ordinal)
                || !Contains(network, requestAddress))
                throw new UnauthorizedAccessException("This client is not allowed to fetch the pending endpoint configuration.");
            if (state.JobId != pending.JobId || state.JobRevision != pending.JobRevision)
                throw new InvalidOperationException("The active job no longer matches the pending endpoint configuration.");

            pending = UpdatePending(endpointId, attemptId, current =>
            {
                var firstFetch = current.ConfigurationFetchedUtc ?? Now;
                return current with
                {
                    ConfigurationFetchedUtc = firstFetch,
                    Status = "awaiting-registration",
                    RegistrationDeadlineUtc = firstFetch.Add(RegistrationTimeout),
                    ExpiresUtc = firstFetch.Add(RegistrationTimeout)
                };
            }) ?? throw new InvalidOperationException("The onboarding attempt changed during the fetch. Request fresh local approval.");
            await PersistFetchedConfigurationAsync(pending);
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
                pending.Network, pending.AttemptId, pending.JobId, pending.JobRevision);
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

    private Task<AppState> PersistFetchedConfigurationAsync(PendingConfiguration pending) => store.UpdateAsync(current =>
    {
        if (!_pending.TryGetValue(pending.EndpointId, out var active) || active.AttemptId != pending.AttemptId || !IsActive(active))
            throw new InvalidOperationException("The onboarding attempt changed before settings could be issued.");
        if (current.JobId != pending.JobId || current.JobRevision != pending.JobRevision)
            throw new InvalidOperationException("The job changed during configuration fetch.");
        var receipts = (current.PcOnboardingReceipts ?? []).ToList();
        if (!receipts.Any(receipt => receipt.AttemptId == pending.AttemptId))
        {
            receipts = receipts.Select(receipt => receipt.EndpointId == pending.EndpointId
                ? receipt with { Status = "superseded" } : receipt).ToList();
            receipts.Add(new(pending.EndpointId, pending.AttemptId, pending.JobId, pending.JobRevision,
                pending.AgentAddress, pending.Network, pending.ConfigurationFetchedUtc!.Value,
                pending.RegistrationDeadlineUtc!.Value, "applying"));
        }
        return current with { PcOnboardingReceipts = receipts };
    });

    public void RecordApprovalRequestStarted(string endpointId, string? attemptId = null)
    {
        if (!_pending.TryGetValue(endpointId, out var pending) || attemptId is not null && pending.AttemptId != attemptId) return;
        UpdatePending(endpointId, pending.AttemptId, current => current.ConfigurationFetchedUtc is not null ? current : current with
        {
            Status = "awaiting-local-approval",
            Message = "Waiting for the endpoint user to approve onboarding locally.",
            RegistrationDeadlineUtc = null
        });
    }

    public WindowsPcRemoteOnboardingState? Status(string endpointId, AppState? state = null)
    {
        Cleanup();
        var receipt = state?.PcOnboardingReceipts?.LastOrDefault(item => item.EndpointId == endpointId);
        if (receipt is not null && (!_pending.TryGetValue(endpointId, out var active) || active.AttemptId == receipt.AttemptId))
            return ReceiptState(receipt);
        if (_pending.TryGetValue(endpointId, out var pending))
        {
            if (pending.RegistrationDeadlineUtc is { } deadline && Now > deadline)
            {
                var original = pending;
                pending = pending with
                {
                    Status = "failed",
                    Message = pending.ConfigurationFetchedUtc is null
                        ? "The PC has not fetched its approved settings. Check its UAC prompt and LAN access, then retry."
                        : "The PC did not finish within five minutes after fetching settings. Its outcome requires reconciliation or fresh onboarding."
                };
                if (!_pending.TryUpdate(endpointId, pending, original)) return Status(endpointId);
            }
            return ToState(pending);
        }
        return _results.TryGetValue(endpointId, out var result) ? result.State : null;
    }

    public void RecordAgentResponse(string endpointId, HttpStatusCode statusCode, string? attemptId = null)
    {
        if (!_pending.TryGetValue(endpointId, out var pending) || attemptId is not null && pending.AttemptId != attemptId) return;
        var original = pending;
        pending = statusCode switch
        {
            HttpStatusCode.Accepted => pending with
            {
                Status = "awaiting-registration",
                Message = "The endpoint user approved onboarding. Waiting for UAC and the elevated utility; its configuration timer begins when settings are fetched.",
                RegistrationDeadlineUtc = pending.RegistrationDeadlineUtc
            },
            HttpStatusCode.Forbidden => pending with
            {
                Status = "denied",
                Message = "The endpoint user denied onboarding. Request a new attempt for fresh local approval.",
                RegistrationDeadlineUtc = null
            },
            _ => pending with
            {
                Status = "failed",
                Message = $"The NDI Configurator PC Agent returned HTTP {(int)statusCode}. Request a new onboarding attempt.",
                RegistrationDeadlineUtc = null
            }
        };
        if (!_pending.TryUpdate(endpointId, pending, original))
        {
            RecordAgentResponse(endpointId, statusCode, attemptId);
            return;
        }
        logger.LogInformation(
            "Remote Windows onboarding agent response for endpoint {EndpointId}: HTTP {StatusCode}; state {State}",
            endpointId,
            (int)statusCode,
            pending.Status);
    }

    public void RecordFailure(string endpointId, string message, string? attemptId = null)
    {
        if (!_pending.TryGetValue(endpointId, out var pending) || attemptId is not null && pending.AttemptId != attemptId) return;
        UpdatePending(endpointId, pending.AttemptId, current => current with
        {
            Status = "failed",
            Message = message,
            RegistrationDeadlineUtc = null
        });
        logger.LogWarning("Remote Windows onboarding failed for endpoint {EndpointId}: {Message}", endpointId, message);
    }

    public void ValidateRegistration(WindowsPcRegistration registration, AppState current)
    {
        var receipt = (current.PcOnboardingReceipts ?? []).LastOrDefault(item => item.EndpointId == registration.EndpointId);
        if (receipt is null || receipt.AttemptId != registration.AttemptId
            || receipt.JobId != registration.JobId || receipt.JobRevision != registration.JobRevision
            || current.JobId != receipt.JobId || current.JobRevision != receipt.JobRevision
            || receipt.Status is not ("applying" or "registered" or "completed")
            || receipt.Status == "applying" && receipt.RegistrationDeadlineUtc <= Now
            || receipt.Candidate is not null && receipt.Candidate.Address != registration.Address
            || (receipt.Network.Mode == "static" && registration.Address != receipt.Network.Address)
            || (receipt.Network.Mode == "unchanged" && registration.Address != receipt.OriginalAddress)
            || (_pending.TryGetValue(registration.EndpointId, out var active)
                && (active.AttemptId != registration.AttemptId || active.Status is "denied" or "failed")))
            throw new InvalidOperationException("Registration does not match the active approved onboarding attempt and job. Request fresh onboarding.");
    }

    public Task<AppState> RegisterAsync(WindowsPcRegistration registration, WindowsPcEndpoint candidate) =>
        store.UpdateAsync(current =>
        {
            ValidateRegistration(registration, current);
            var existing = current.WindowsPcs?.FirstOrDefault(pc => pc.EndpointId == registration.EndpointId);
            if (existing?.IsServerPc == true) throw new InvalidOperationException("Remote registration cannot replace this server PC.");
            var receipts = current.PcOnboardingReceipts!.Select(receipt =>
                receipt.AttemptId == registration.AttemptId && receipt.Status != "completed"
                    ? receipt with { Status = "registered", Candidate = candidate with { RegisteredUtc = existing?.RegisteredUtc ?? candidate.RegisteredUtc } }
                    : receipt).ToArray();
            // A registration acknowledgement is not the PC's final outcome.
            return current with { PcOnboardingReceipts = receipts };
        });

    public async Task<WindowsPcOnboardingOutcomeResult> ReportOutcomeAsync(WindowsPcOnboardingOutcome request, string sourceAddress)
    {
        if (!Guid.TryParse(request.EndpointId, out var endpoint) || endpoint == Guid.Empty
            || !Guid.TryParse(request.AttemptId, out var attempt) || attempt == Guid.Empty
            || request.Outcome is not ("completed" or "aborted" or "recovery-required"))
            throw new InvalidOperationException("Invalid PC onboarding outcome.");
        var result = "superseded";
        await store.UpdateAsync(current =>
        {
            var receipt = current.PcOnboardingReceipts?.FirstOrDefault(item => item.AttemptId == request.AttemptId
                && item.EndpointId == request.EndpointId && item.JobId == request.JobId && item.JobRevision == request.JobRevision)
                ?? throw new InvalidOperationException("This onboarding receipt is unavailable. Request fresh onboarding.");
            var selected = NetworkAddressing.ResolveLocalInterface(current.SelectedNetworkAdapterId, current.SelectedNetworkAddress);
            if (selected is null || !Contains(selected, sourceAddress)
                || !(receipt.OriginalAddress == sourceAddress || receipt.Candidate?.Address == sourceAddress
                    || agents.Snapshot().Any(agent => agent.EndpointId == request.EndpointId && agent.Address == sourceAddress)))
                throw new UnauthorizedAccessException("This address cannot report the PC's onboarding outcome.");
            if (current.JobId != request.JobId || current.JobRevision != request.JobRevision
                || receipt.Status == "superseded"
                || (_pending.TryGetValue(request.EndpointId, out var pending) && pending.AttemptId != request.AttemptId))
                result = "superseded";
            else if (receipt.Status is "completed" or "aborted" or "recovery-required")
                result = receipt.Status; // Terminal outcomes are idempotent and cannot be undone by late messages.
            else if (request.Outcome == "completed" && receipt.Candidate is null)
                throw new InvalidOperationException("Register the applied settings before confirming completion.");
            else result = request.Outcome;
            var pcs = current.WindowsPcs;
            if (result == "completed" && receipt.Status != "completed")
                pcs = (pcs ?? []).Where(pc => pc.EndpointId != request.EndpointId)
                    .Append(receipt.Candidate!).OrderBy(pc => pc.Hostname, StringComparer.OrdinalIgnoreCase).ToArray();
            return current with
            {
                WindowsPcs = pcs,
                PcOnboardingReceipts = current.PcOnboardingReceipts!.Select(item => item.AttemptId == request.AttemptId
                    ? item with { Status = result } : item).ToArray()
            };
        });
        if (_pending.TryGetValue(request.EndpointId, out var active) && active.AttemptId == request.AttemptId)
            ((ICollection<KeyValuePair<string, PendingConfiguration>>)_pending).Remove(new(request.EndpointId, active));
        return new(request.AttemptId, request.JobId, request.JobRevision, result);
    }

    public void RecordRegistration(WindowsPcEndpoint endpoint, string attemptId)
    {
        if (!_pending.TryGetValue(endpoint.EndpointId, out var pending) || pending.AttemptId != attemptId) return;
        if (!((ICollection<KeyValuePair<string, PendingConfiguration>>)_pending).Remove(new(endpoint.EndpointId, pending))) return;
        var now = Now;
        var state = new WindowsPcRemoteOnboardingState(
            endpoint.EndpointId,
            "awaiting-registration",
            $"The PC registered at {endpoint.Address}; waiting for its final outcome.",
            pending.RequestedUtc,
            pending.RegistrationDeadlineUtc,
            now.Add(ResultLifetime),
            endpoint.Address,
            pending.ConfigurationFetchedUtc,
            pending.AttemptId);
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
        var now = Now;
        foreach (var item in _pending.Where(item => item.Value.ExpiresUtc <= now).ToArray())
        {
            var expired = item.Value;
            if (!((ICollection<KeyValuePair<string, PendingConfiguration>>)_pending).Remove(item)) continue;
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
            ((ICollection<KeyValuePair<string, RecentResult>>)_results).Remove(item);
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

    private static HashSet<string> KnownInfrastructure(AppState state, WindowsPcAgentSnapshot agent, WindowsPcRemoteNetworkConfiguration desired)
    {
        var addresses = NetworkAddressing.GetLocalInterfaces().Select(n => n.Address)
            .Concat(agent.NetworkConfiguration?.DefaultGateways ?? [])
            .Concat(new[] { state.LastJob?.NdiDiscoveryServerIp, state.LastJob?.KiloLinkServerIp, desired.DefaultGateway }
                .OfType<string>());
        return addresses.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal);
    }

    private static void RequireRemoteCapabilities(WindowsPcAgentSnapshot agent)
    {
        if (!agent.Capabilities.Contains("onboarding-outcome-v1", StringComparer.Ordinal)
            || !agent.Capabilities.Contains("onboarding-attempt-v1", StringComparer.Ordinal)
            || !agent.Capabilities.Contains("remote-onboarding-v2", StringComparer.Ordinal)
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
        ConfigurationFetchedUtc: pending.ConfigurationFetchedUtc,
        AttemptId: pending.AttemptId);

    private bool IsActive(PendingConfiguration pending) =>
        pending.Status is "staged" or "awaiting-local-approval" or "awaiting-registration"
        && pending.ExpiresUtc > Now
        && (pending.RegistrationDeadlineUtc is null || pending.RegistrationDeadlineUtc > Now);

    private PendingConfiguration? UpdatePending(string endpointId, string? attemptId, Func<PendingConfiguration, PendingConfiguration> update)
    {
        while (_pending.TryGetValue(endpointId, out var current) && current.AttemptId == attemptId && IsActive(current))
        {
            var changed = update(current);
            if (_pending.TryUpdate(endpointId, changed, current)) return changed;
        }
        return null;
    }

    private WindowsPcRemoteOnboardingState ReceiptState(WindowsPcOnboardingReceipt receipt)
    {
        var completed = receipt.Status == "completed";
        var waiting = receipt.Status is "applying" or "registered" && receipt.RegistrationDeadlineUtc > Now;
        var status = completed ? "completed" : waiting ? "awaiting-registration" : "failed";
        var message = completed ? "Onboarding completed and the PC confirmed its applied settings."
            : waiting ? "The PC is applying settings or confirming its final outcome."
            : receipt.Status == "aborted" ? "The PC rolled back onboarding. Request a new attempt."
            : receipt.Status == "superseded" ? "This attempt belongs to a replaced job or newer onboarding request."
            : "The PC outcome needs reconciliation or local repair. It has not been marked onboarded by this attempt.";
        return new(receipt.EndpointId, status, message, receipt.StartedUtc, receipt.RegistrationDeadlineUtc,
            receipt.RegistrationDeadlineUtc, completed ? receipt.Candidate?.Address : null, receipt.StartedUtc, receipt.AttemptId);
    }

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
        DateTimeOffset? ConfigurationFetchedUtc,
        string AttemptId, string JobId, string JobRevision);

    private sealed record RecentResult(WindowsPcRemoteOnboardingState State, DateTimeOffset ExpiresUtc);
}
