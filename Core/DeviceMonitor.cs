using System.Collections.Concurrent;
using NDIJobConfigurator.Devices;

namespace NDIJobConfigurator.Core;

public sealed class DeviceMonitor(
    AppStateStore store,
    DeviceClientFactory factory,
    WindowsPcAgentService pcAgents,
    ILogger<DeviceMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan N6ModeStartupGrace = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, DateTimeOffset> n6ModeUnavailableSince = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Device monitor pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var snapshot = await store.ReadAsync();
        var results = new ConcurrentDictionary<string, DevicePollResult>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            snapshot.Devices,
            new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
            async (device, token) =>
            {
                ManagedDevice updated;
                try
                {
                    if (device.IsSimulation())
                    {
                        updated = device with
                        {
                            Health = DeviceHealth.Online,
                            LastSeenUtc = DateTimeOffset.UtcNow,
                            LastError = null
                        };
                    }
                    else if (device.ManagementState is "first-login" or "api-disabled")
                    {
                        var refreshed = await factory.ProbeAsync(device.IpAddress, device.Credentials, token)
                            ?? throw new DeviceApiException($"Factory N6 at {device.IpAddress} is no longer reachable.");
                        updated = refreshed with
                        {
                            IsOnboarded = device.IsOnboarded,
                            Health = DeviceHealth.Online,
                            LastError = null
                        };
                    }
                    else
                    {
                        var persisted = ResolveN6PersistedIdentity(snapshot, device);
                        var refreshed = NormalizeN6ColdStartRole(persisted, await factory.Create(device).ReadAsync(token));
                        updated = refreshed with
                        {
                            Id = persisted.Id,
                            IsOnboarded = device.IsOnboarded,
                            NdiGroup = device.NdiGroup,
                            NdiChannelName = device.NdiChannelName,
                            HdmiDisplayConnected = device.HdmiDisplayConnected,
                            HdmiOutputResolution = device.HdmiOutputResolution,
                            MulticastConfigured = device.IsTeleTool() ? refreshed.MulticastConfigured : device.MulticastConfigured,
                            MulticastInUse = device.IsTeleTool() ? refreshed.MulticastInUse : device.MulticastConfigured,
                            MulticastNetPrefix = refreshed.MulticastNetPrefix ?? device.MulticastNetPrefix,
                            MulticastNetmask = refreshed.MulticastNetmask ?? device.MulticastNetmask,
                            MulticastTtl = refreshed.MulticastTtl ?? device.MulticastTtl,
                            MulticastLastError = refreshed.MulticastLastError ?? device.MulticastLastError,
                            LastError = null
                        };
                    }
                    if (!device.IsOnboarded && device.Health == DeviceHealth.Error && !string.IsNullOrWhiteSpace(device.LastError))
                    {
                        updated = updated with
                        {
                            Health = DeviceHealth.Error,
                            LastError = device.LastError
                        };
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DeviceApiException
                                               or InvalidOperationException or System.Text.Json.JsonException)
                {
                    var preserveOnboardingFailure = !device.IsOnboarded
                        && device.Health == DeviceHealth.Error
                        && !string.IsNullOrWhiteSpace(device.LastError);
                    updated = device with
                    {
                        Health = preserveOnboardingFailure ? DeviceHealth.Error : DeviceHealth.Offline,
                        MulticastInUse = false,
                        LastError = preserveOnboardingFailure ? device.LastError : ex.Message
                    };
                }
                results[device.Id] = new(device, updated);
            });
        var remoteWindowsResults = await PollWindowsPcsAsync(snapshot, ct);

        await store.UpdateAsync(current => ApplyResults(current, results.Values, remoteWindowsResults));
    }

    private static ManagedDevice ResolveN6PersistedIdentity(AppState state, ManagedDevice device)
    {
        if (device.Family != DeviceFamily.N6) return device;

        var assignment = state.Multicast?.Assignments.FirstOrDefault(candidate =>
            string.Equals(candidate.EndpointId, device.Id, StringComparison.Ordinal)
            || string.Equals(candidate.Address, device.IpAddress, StringComparison.Ordinal));
        if (assignment is null) return device;

        var role = device.Role;
        if (role == DeviceRole.Unknown && assignment.Role != DeviceRole.Unknown)
        {
            role = assignment.Role;
        }
        return device with { Id = assignment.EndpointId, Role = role };
    }

    private ManagedDevice NormalizeN6ColdStartRole(ManagedDevice previous, ManagedDevice refreshed)
    {
        if (refreshed.Family != DeviceFamily.N6 || refreshed.ManagementState is not ("mode-starting" or "mode-recovery-required"))
        {
            n6ModeUnavailableSince.TryRemove(previous.Id, out _);
            return refreshed;
        }

        var role = refreshed.Role != DeviceRole.Unknown
            ? refreshed.Role
            : previous.Role;
        if (role == DeviceRole.Unknown) return refreshed;

        var unavailableSince = n6ModeUnavailableSince.GetOrAdd(previous.Id, DateTimeOffset.UtcNow);
        var inStartupGrace = DateTimeOffset.UtcNow - unavailableSince < N6ModeStartupGrace;
        return refreshed with
        {
            Role = role,
            ManagementState = inStartupGrace ? "mode-starting" : "mode-fallback",
            ManagementMessage = inStartupGrace
                ? $"N6 is online as the last confirmed {role.ToString().ToLowerInvariant()} while its mode service completes a cold start."
                : $"N6 is online as the confirmed {role.ToString().ToLowerInvariant()}; monitoring is using the saved device assignment because the firmware mode endpoint is unavailable."
        };
    }

    private static AppState ApplyResults(
        AppState current,
        IEnumerable<DevicePollResult> results,
        IEnumerable<RemoteWindowsPollResult> remoteWindowsResults)
    {
        var devices = current.Devices.ToArray();
        var multicast = current.Multicast;
        var assignments = multicast?.Assignments.ToArray();
        var devicesChanged = false;
        var assignmentsChanged = false;
        var windowsPcs = (current.WindowsPcs ?? []).ToArray();
        var remoteWindowsChanged = false;

        foreach (var result in results)
        {
            var index = Array.FindIndex(devices, device => device.Id == result.Original.Id);
            if (index < 0 || !SnapshotStillCurrent(devices[index], result.Original)) continue;

            var monitored = result.Updated with
            {
                Credentials = devices[index].Credentials,
                LicenseAccepted = devices[index].LicenseAccepted,
                IsOnboarded = devices[index].IsOnboarded
            };
            if (result.Original.IsTeleTool() && CanMonitorMulticast(multicast) && assignments is not null)
            {
                var assignmentIndex = Array.FindIndex(
                    assignments,
                    assignment => assignment.EndpointId == monitored.Id
                        || assignment.EndpointId == result.Original.Id);
                if (assignmentIndex >= 0 && CanMonitorAssignment(assignments[assignmentIndex]))
                {
                    var assignment = assignments[assignmentIndex];
                    var matches = monitored.Health == DeviceHealth.Online
                        && monitored.MulticastConfigured
                        && string.Equals(monitored.MulticastNetPrefix, assignment.NetPrefix, StringComparison.Ordinal)
                        && string.Equals(monitored.MulticastNetmask, assignment.Netmask, StringComparison.Ordinal)
                        && monitored.MulticastTtl == assignment.Ttl;
                    var error = matches
                        ? null
                        : monitored.Health != DeviceHealth.Online
                            ? monitored.LastError ?? "TeleTool multicast status is unavailable."
                            : !monitored.MulticastConfigured
                                ? "TeleTool reports multicast disabled. Reapply multicast setup."
                                : $"TeleTool multicast settings changed. Expected {assignment.NetPrefix}/{assignment.Netmask}, TTL {assignment.Ttl}; device reports {monitored.MulticastNetPrefix ?? "unset"}/{monitored.MulticastNetmask ?? "unset"}, TTL {monitored.MulticastTtl?.ToString() ?? "unset"}.";
                    monitored = monitored with { MulticastLastError = error };
                    var refreshed = assignment with
                    {
                        Status = monitored.Health != DeviceHealth.Online ? "error" : matches ? "applied" : "drifted",
                        InUse = matches && monitored.MulticastInUse,
                        Error = error
                    };
                    if (refreshed != assignment)
                    {
                        assignments[assignmentIndex] = refreshed;
                        assignmentsChanged = true;
                    }
                }
            }

            if (monitored != devices[index])
            {
                devices[index] = monitored;
                devicesChanged = true;
            }
        }

        foreach (var result in remoteWindowsResults)
        {
            var index = Array.FindIndex(
                windowsPcs,
                endpoint => string.Equals(
                    endpoint.EndpointId,
                    result.Original.EndpointId,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0 || !SnapshotStillCurrent(windowsPcs[index], result.Original)) continue;
            if (CanMonitorMulticast(multicast) && assignments is not null
                && result.Updated.AgentCapabilities?.Contains("multicast-config-v1", StringComparer.Ordinal) == true)
            {
                var assignmentIndex = Array.FindIndex(assignments, assignment =>
                    string.Equals(assignment.EndpointId, result.Original.EndpointId, StringComparison.OrdinalIgnoreCase));
                if (assignmentIndex >= 0 && CanMonitorAssignment(assignments[assignmentIndex]))
                {
                    var assignment = assignments[assignmentIndex];
                    var reported = result.LiveStatus?.MulticastConfiguration;
                    var matches = reported is not null
                        && string.Equals(reported.Mode, "multicast", StringComparison.Ordinal)
                        && reported.SendEnabled
                        && reported.ReceiveEnabled
                        && string.Equals(reported.AdapterId, result.LiveStatus?.AdapterId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(reported.NetPrefix, assignment.NetPrefix, StringComparison.Ordinal)
                        && string.Equals(reported.Netmask, assignment.Netmask, StringComparison.Ordinal)
                        && reported.Ttl == assignment.Ttl
                        && string.Equals(reported.JobName, multicast!.JobName, StringComparison.Ordinal);
                    var error = matches
                        ? null
                        : result.Updated.ConnectivityStatus == "offline"
                            ? "NDI Configurator PC Agent multicast status is unavailable because the endpoint is offline."
                            : reported is null
                                ? "NDI Configurator PC Agent did not report NDI Access Manager multicast status. Reapply multicast setup."
                                : $"Remote NDI Access Manager settings changed. Expected {assignment.NetPrefix}/{assignment.Netmask}, TTL {assignment.Ttl}.";
                    var refreshed = assignment with
                    {
                        Status = matches ? "applied" : "drifted",
                        InUse = matches && reported!.InUse,
                        Error = error
                    };
                    if (refreshed != assignment)
                    {
                        assignments[assignmentIndex] = refreshed;
                        assignmentsChanged = true;
                    }
                }
            }
            if (SnapshotStillCurrent(windowsPcs[index], result.Updated)) continue;
            windowsPcs[index] = result.Updated;
            remoteWindowsChanged = true;
        }

        if (!devicesChanged && !assignmentsChanged && !remoteWindowsChanged) return current;
        if (multicast is not null && assignments is not null && assignmentsChanged)
        {
            multicast = multicast with
            {
                Assignments = assignments,
                Status = assignments.All(assignment => assignment.Status is "applied" or "reserved") ? "completed" : "partial"
            };
        }
        return current with
        {
            Devices = devicesChanged ? devices : current.Devices,
            Multicast = multicast,
            WindowsPcs = remoteWindowsChanged ? windowsPcs : current.WindowsPcs
        };
    }

    private async Task<IReadOnlyList<RemoteWindowsPollResult>> PollWindowsPcsAsync(
        AppState state,
        CancellationToken ct)
    {
        var endpoints = state.WindowsPcs ?? [];
        if (endpoints.Count == 0) return [];

        var selectedNetwork = NetworkAddressing.ResolveLocalInterface(
            state.SelectedNetworkAdapterId,
            state.SelectedNetworkAddress);
        var discovered = pcAgents.Snapshot()
            .ToDictionary(agent => agent.EndpointId, StringComparer.OrdinalIgnoreCase);
        var results = new ConcurrentBag<RemoteWindowsPollResult>();
        await Parallel.ForEachAsync(
            endpoints,
            new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
            async (endpoint, token) =>
            {
                var checkedUtc = DateTimeOffset.UtcNow;
                WindowsPcAgentStatus? live = null;
                try
                {
                    if (selectedNetwork is not null &&
                        discovered.TryGetValue(endpoint.EndpointId, out var agent) &&
                        agent.Capabilities.Contains("status-v1", StringComparer.Ordinal))
                    {
                        live = await pcAgents.TryReadStatusAsync(
                            agent.Address,
                            agent.ApiPort,
                            endpoint.EndpointId,
                            selectedNetwork,
                            token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    logger.LogDebug("NDI Configurator PC Agent status poll timed out for {EndpointId}", endpoint.EndpointId);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or InvalidOperationException
                    or System.Text.Json.JsonException)
                {
                    logger.LogDebug(ex, "NDI Configurator PC Agent status poll failed for {EndpointId}", endpoint.EndpointId);
                }

                var reachable = live is not null;
                var failures = reachable
                    ? 0
                    : Math.Min(endpoint.ConsecutiveConnectivityFailures + 1, 3);
                var connectivityStatus = reachable
                    ? "online"
                    : failures >= 3
                        ? "offline"
                        : "stale";
                var updated = endpoint with
                {
                    PreferredInterfaceConfigured = live?.NdiConfiguration?.PreferredInterfaceConfigured ?? endpoint.PreferredInterfaceConfigured,
                    Error = live?.NdiConfiguration is { } ndi && state.LastJob is { } job
                        ? NdiConfigurationError(ndi, job) : endpoint.Error,
                    Hostname = live?.Hostname ?? endpoint.Hostname,
                    Address = live?.Address ?? endpoint.Address,
                    AdapterName = live?.AdapterName ?? endpoint.AdapterName,
                    PrefixLength = live?.PrefixLength ?? endpoint.PrefixLength,
                    NdiToolsVersion = live?.NdiToolsVersion ?? endpoint.NdiToolsVersion,
                    OperatingSystemVersion = live?.OperatingSystemVersion ?? endpoint.OperatingSystemVersion,
                    LastConnectivityCheckUtc = checkedUtc,
                    LastSeenUtc = reachable ? checkedUtc : endpoint.LastSeenUtc,
                    ConsecutiveConnectivityFailures = failures,
                    ConnectivityStatus = connectivityStatus,
                    AgentSchemaVersion = live?.SchemaVersion ?? endpoint.AgentSchemaVersion,
                    AgentVersion = live?.AgentVersion ?? endpoint.AgentVersion,
                    AgentCapabilities = discovered.GetValueOrDefault(endpoint.EndpointId)?.Capabilities ?? endpoint.AgentCapabilities,
                    AgentUptimeSeconds = live?.AgentUptimeSeconds ?? endpoint.AgentUptimeSeconds,
                    MachineUptimeSeconds = live?.MachineUptimeSeconds ?? endpoint.MachineUptimeSeconds,
                    PhysicalMemoryTotalBytes = live?.PhysicalMemoryTotalBytes ?? endpoint.PhysicalMemoryTotalBytes,
                    PhysicalMemoryAvailableBytes = live?.PhysicalMemoryAvailableBytes ?? endpoint.PhysicalMemoryAvailableBytes,
                    SystemDriveTotalBytes = live?.SystemDriveTotalBytes ?? endpoint.SystemDriveTotalBytes,
                    SystemDriveFreeBytes = live?.SystemDriveFreeBytes ?? endpoint.SystemDriveFreeBytes,
                    AgentObservedUtc = live?.ObservedUtc ?? endpoint.AgentObservedUtc
                };
                results.Add(new(endpoint, updated, live));
            });
        return results.ToArray();
    }

    private static bool SnapshotStillCurrent(ManagedDevice latest, ManagedDevice original) => latest == original;

    internal static string? NdiConfigurationError(WindowsPcNdiConfiguration ndi, LastJob job) =>
        ndi.PreferredInterfaceConfigured
        && ndi.SendGroups?.Contains(job.JobName, StringComparer.OrdinalIgnoreCase) == true
        && ndi.ReceiveGroups?.Contains(job.JobName, StringComparer.OrdinalIgnoreCase) == true
        && string.Equals(ndi.DiscoveryServer, job.NdiDiscoveryServerIp, StringComparison.Ordinal)
            ? null : "The PC Agent reports NDI interface, job group, or discovery-server drift. Reapply PC onboarding.";

    private static bool SnapshotStillCurrent(WindowsPcEndpoint latest, WindowsPcEndpoint original) =>
        latest with { AgentCapabilities = null } == original with { AgentCapabilities = null }
        && (latest.AgentCapabilities ?? []).SequenceEqual(original.AgentCapabilities ?? [], StringComparer.Ordinal);

    private static bool CanMonitorMulticast(MulticastConfiguration? configuration) =>
        configuration?.Status is "completed" or "partial";

    private static bool CanMonitorAssignment(MulticastAssignment assignment) =>
        assignment.Status is not ("unicast" or "reserved");

    private sealed record DevicePollResult(ManagedDevice Original, ManagedDevice Updated);
    private sealed record RemoteWindowsPollResult(
        WindowsPcEndpoint Original,
        WindowsPcEndpoint Updated,
        WindowsPcAgentStatus? LiveStatus);

}
