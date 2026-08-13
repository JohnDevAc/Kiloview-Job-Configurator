using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using KiloviewSetup.Devices;

namespace KiloviewSetup.Core;

public sealed class DeviceMonitor(
    AppStateStore store,
    DeviceClientFactory factory,
    NdiAccessManagerService accessManager,
    ILogger<DeviceMonitor> logger) : BackgroundService
{
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
                        var refreshed = await factory.Create(device).ReadAsync(token);
                        updated = refreshed with
                        {
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
        var remoteWindowsResults = await PollRemoteWindowsPcsAsync(snapshot, ct);

        AccessManagerPollResult? accessManagerResult = null;
        try
        {
            accessManagerResult = await PollAccessManagerAsync(snapshot, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read local NDI Access Manager multicast status");
        }

        LocalPcPollResult? localPcResult = null;
        try
        {
            localPcResult = await PollLocalPcAsync(snapshot, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the preferred local NDI interface");
        }
        await store.UpdateAsync(current => ApplyResults(
            current,
            results.Values,
            remoteWindowsResults,
            accessManagerResult,
            localPcResult));
    }

    private static AppState ApplyResults(
        AppState current,
        IEnumerable<DevicePollResult> results,
        IEnumerable<RemoteWindowsPollResult> remoteWindowsResults,
        AccessManagerPollResult? accessManagerResult,
        LocalPcPollResult? localPcResult)
    {
        var devices = current.Devices.ToArray();
        var multicast = current.Multicast;
        var assignments = multicast?.Assignments.ToArray();
        var devicesChanged = false;
        var assignmentsChanged = false;
        var remoteWindowsPcs = (current.RemoteWindowsPcs ?? []).ToArray();
        var remoteWindowsChanged = false;

        foreach (var result in results)
        {
            var index = Array.FindIndex(devices, device => device.Id == result.Original.Id);
            if (index < 0 || !SnapshotStillCurrent(devices[index], result.Original)) continue;

            var monitored = result.Updated;
            if (result.Original.IsTeleTool() && multicast is not null && assignments is not null)
            {
                var assignmentIndex = Array.FindIndex(
                    assignments,
                    assignment => assignment.EndpointId == result.Original.Id);
                if (assignmentIndex >= 0)
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
                remoteWindowsPcs,
                endpoint => string.Equals(
                    endpoint.EndpointId,
                    result.Original.EndpointId,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0 || remoteWindowsPcs[index] != result.Original) continue;
            if (remoteWindowsPcs[index] == result.Updated) continue;
            remoteWindowsPcs[index] = result.Updated;
            remoteWindowsChanged = true;
        }

        if (accessManagerResult is not null && assignments is not null && multicast is not null
            && string.Equals(multicast.JobName, accessManagerResult.JobName, StringComparison.Ordinal))
        {
            var localIndex = Array.FindIndex(assignments, assignment => assignment.EndpointId == "local-pc");
            if (localIndex >= 0 && assignments[localIndex] == accessManagerResult.Original
                && assignments[localIndex] != accessManagerResult.Updated)
            {
                assignments[localIndex] = accessManagerResult.Updated;
                assignmentsChanged = true;
            }
        }

        var localPc = current.LocalPc;
        var localPcChanged = false;
        if (localPcResult is not null
            && localPc == localPcResult.Original
            && localPc != localPcResult.Updated)
        {
            localPc = localPcResult.Updated;
            localPcChanged = true;
        }

        if (!devicesChanged && !assignmentsChanged && !localPcChanged && !remoteWindowsChanged) return current;
        if (multicast is not null && assignments is not null && assignmentsChanged)
        {
            multicast = multicast with
            {
                Assignments = assignments,
                Status = assignments.All(assignment => assignment.Status == "applied") ? "completed" : "partial"
            };
        }
        return current with
        {
            Devices = devicesChanged ? devices : current.Devices,
            Multicast = multicast,
            LocalPc = localPc,
            RemoteWindowsPcs = remoteWindowsChanged ? remoteWindowsPcs : current.RemoteWindowsPcs
        };
    }

    private static async Task<IReadOnlyList<RemoteWindowsPollResult>> PollRemoteWindowsPcsAsync(
        AppState state,
        CancellationToken ct)
    {
        var endpoints = state.RemoteWindowsPcs ?? [];
        if (endpoints.Count == 0) return [];

        var results = new ConcurrentBag<RemoteWindowsPollResult>();
        await Parallel.ForEachAsync(
            endpoints,
            new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
            async (endpoint, token) =>
            {
                var checkedUtc = DateTimeOffset.UtcNow;
                var reachable = false;
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(
                        endpoint.Address,
                        TimeSpan.FromMilliseconds(900),
                        cancellationToken: token);
                    reachable = reply.Status == IPStatus.Success;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is PingException
                    or InvalidOperationException
                    or System.Net.Sockets.SocketException)
                {
                    reachable = false;
                }

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
                    LastConnectivityCheckUtc = checkedUtc,
                    LastSeenUtc = reachable ? checkedUtc : endpoint.LastSeenUtc,
                    ConsecutiveConnectivityFailures = failures,
                    ConnectivityStatus = connectivityStatus
                };
                results.Add(new(endpoint, updated));
            });
        return results.ToArray();
    }

    private async Task<LocalPcPollResult?> PollLocalPcAsync(AppState state, CancellationToken ct)
    {
        var localPc = state.LocalPc;
        if (localPc is null) return null;

        var selected = NetworkAddressing.ResolveLocalInterface(
            state.SelectedNetworkAdapterId,
            state.SelectedNetworkAddress);
        if (selected is null)
        {
            var unavailable = localPc with
            {
                PreferredInterfaceConfigured = false,
                Status = "drifted",
                Error = "The selected onboarding network adapter is no longer active.",
                OperatingSystemVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                NdiToolsVersion = accessManager.RuntimeVersion
            };
            return unavailable == localPc ? null : new(localPc, unavailable);
        }

        var status = await accessManager.ReadPreferredInterfaceStatusAsync(selected.Address, ct);
        var updated = localPc with
        {
            AdapterId = selected.Id,
            AdapterName = selected.Name,
            Address = selected.Address,
            PrefixLength = selected.PrefixLength,
            PreferredInterfaceConfigured = status.Configured,
            Status = status.Configured ? "applied" : "drifted",
            Error = status.Configured ? null : status.Error,
            OperatingSystemVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            NdiToolsVersion = accessManager.RuntimeVersion
        };
        return updated == localPc ? null : new(localPc, updated);
    }

    private async Task<AccessManagerPollResult?> PollAccessManagerAsync(AppState state, CancellationToken ct)
    {
        var multicast = state.Multicast;
        var local = multicast?.Assignments.FirstOrDefault(assignment => assignment.EndpointId == "local-pc");
        if (multicast is null || local is null) return null;

        var status = await accessManager.ReadStatusAsync(
            local.NetPrefix,
            local.Netmask,
            local.Ttl,
            ct,
            multicast.JobName,
            state.LastJob?.NdiDiscoveryServerIp,
            state.LocalPc?.Address ?? local.Address);
        var error = status.Configured
            ? null
            : status.Error ?? (status.AccessManagerRunning
                ? "NDI Access Manager is open and its multicast settings do not match this job. Close it, then reapply multicast setup."
                : $"NDI Access Manager preferred interface, multicast, NDI group, or Discovery Server settings changed. Current send range: {status.NetPrefix ?? "disabled"} / {status.Netmask ?? "not set"}, TTL {status.Ttl?.ToString() ?? "not set"}. Reapply multicast setup.");
        var refreshed = local with
        {
            Status = status.Configured ? "applied" : "drifted",
            InUse = status.Configured,
            Error = error
        };
        return refreshed == local ? null : new(local, refreshed, multicast.JobName);
    }

    private static bool SnapshotStillCurrent(ManagedDevice latest, ManagedDevice original) =>
        string.Equals(latest.IpAddress, original.IpAddress, StringComparison.Ordinal)
        && latest.IsOnboarded == original.IsOnboarded
        && latest.IsStatic == original.IsStatic
        && latest.Role == original.Role
        && string.Equals(latest.Hostname, original.Hostname, StringComparison.Ordinal)
        && string.Equals(latest.NdiChannelName, original.NdiChannelName, StringComparison.Ordinal)
        && string.Equals(latest.NdiGroup, original.NdiGroup, StringComparison.Ordinal);

    private sealed record DevicePollResult(ManagedDevice Original, ManagedDevice Updated);
    private sealed record RemoteWindowsPollResult(
        RemoteWindowsPcEndpoint Original,
        RemoteWindowsPcEndpoint Updated);
    private sealed record AccessManagerPollResult(
        MulticastAssignment Original,
        MulticastAssignment Updated,
        string JobName);
    private sealed record LocalPcPollResult(
        LocalPcEndpoint Original,
        LocalPcEndpoint Updated);
}
