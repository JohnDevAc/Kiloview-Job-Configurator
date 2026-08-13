using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using KiloviewSetup.Devices;

namespace KiloviewSetup.Core;

public sealed class MulticastService(
    AppStateStore store,
    DeviceClientFactory factory,
    NdiAccessManagerService accessManager,
    WindowsPcAgentService pcAgents,
    EncoderThumbnailService thumbnails,
    ILogger<MulticastService> logger)
{
    private const int AllocationPrefixLength = 24;
    private const string AllocationNetmask = "255.255.255.0";
    private const uint AllocationSize = 256;
    private static readonly uint ScopeStart = NetworkAddressing.ToUInt(IPAddress.Parse("239.192.0.0"));
    private static readonly uint ScopeEnd = NetworkAddressing.ToUInt(IPAddress.Parse("239.195.255.255"));

    public async Task<MulticastConfiguration> BuildPlanAsync(MulticastSetupRequest request, CancellationToken ct)
    {
        if (request.Ttl is < 1 or > 255) throw new ArgumentException("Multicast TTL must be between 1 and 255.");
        var state = await store.ReadAsync();
        var job = state.LastJob ?? throw new InvalidOperationException("Complete onboarding before configuring multicast.");
        var devices = state.Devices.Where(device => device.IsOnboarded).OrderBy(device => device.Id, StringComparer.Ordinal).ToArray();
        var remoteWindowsPcs = (state.RemoteWindowsPcs ?? [])
            .OrderBy(endpoint => endpoint.EndpointId, StringComparer.Ordinal)
            .ToArray();
        if (devices.Length == 0 && remoteWindowsPcs.Length == 0 && !request.IncludeLocalPc)
            throw new InvalidOperationException("There are no onboarded devices or Windows PC endpoints to configure.");

        var senders = devices.Where(device => device.Role == DeviceRole.Encoder).ToArray();
        var slots = senders.Length + remoteWindowsPcs.Length + (request.IncludeLocalPc ? 1 : 0);
        var poolPrefixLength = PoolPrefixLength(slots);
        var poolSize = 1u << (32 - poolPrefixLength);
        var poolStart = SelectPool(job.JobName, poolSize, request.Regenerate, state.Multicast);
        var poolMask = PrefixMask(poolPrefixLength);
        var selectedNetwork = request.IncludeLocalPc
            ? NetworkAddressing.ResolveLocalInterface(
                state.SelectedNetworkAdapterId,
                state.SelectedNetworkAddress)
                ?? throw new InvalidOperationException(
                    "The onboarding network adapter is no longer active. Return to New onboarding and select an active adapter.")
            : null;
        var localAddress = selectedNetwork?.Address ?? "127.0.0.1";
        var assignments = new List<MulticastAssignment>();
        var slot = 0u;

        foreach (var device in devices)
        {
            var sender = device.Role == DeviceRole.Encoder;
            var prefix = sender ? SenderPrefix(poolStart, slot++) : null;
            assignments.Add(new(
                device.Id,
                device.Hostname,
                device.IpAddress,
                device.Family.ToString(),
                device.Role,
                sender,
                device.Role == DeviceRole.Decoder,
                prefix,
                sender ? AllocationNetmask : null,
                request.Ttl));
        }

        foreach (var endpoint in remoteWindowsPcs)
        {
            assignments.Add(new(
                endpoint.EndpointId,
                endpoint.Hostname,
                endpoint.Address,
                "WindowsPC",
                DeviceRole.Encoder,
                true,
                true,
                SenderPrefix(poolStart, slot++),
                AllocationNetmask,
                request.Ttl));
        }

        if (request.IncludeLocalPc)
        {
            assignments.Add(new(
                "local-pc",
                Environment.MachineName,
                localAddress,
                "WindowsPC",
                DeviceRole.Encoder,
                true,
                true,
                SenderPrefix(poolStart, slot),
                AllocationNetmask,
                request.Ttl));
        }

        ValidateAssignments(poolStart, poolSize, assignments);
        return new(
            Guid.NewGuid(),
            job.JobName,
            NetworkAddressing.FromUInt(poolStart).ToString(),
            NetworkAddressing.FromUInt(poolMask).ToString(),
            NetworkAddressing.FromUInt(poolStart + poolSize - 1).ToString(),
            AllocationNetmask,
            request.Ttl,
            request.IncludeLocalPc,
            accessManager.Detected,
            assignments,
            "planned",
            DateTimeOffset.UtcNow,
            AccessManagerRunning: accessManager.IsRunning);
    }

    public async Task<MulticastApplyResult> ApplyAsync(MulticastConfiguration plan, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var job = state.LastJob ?? throw new InvalidOperationException("Complete onboarding before configuring multicast.");
        if (!string.Equals(plan.JobName, job.JobName, StringComparison.Ordinal))
            throw new InvalidOperationException("The job changed after this multicast plan was generated. Generate a new plan.");
        if (plan.Ttl is < 1 or > 255) throw new ArgumentException("Multicast TTL must be between 1 and 255.");

        var devices = state.Devices.Where(device => device.IsOnboarded).ToDictionary(device => device.Id, StringComparer.Ordinal);
        var remoteWindowsPcs = (state.RemoteWindowsPcs ?? [])
            .ToDictionary(endpoint => endpoint.EndpointId, StringComparer.OrdinalIgnoreCase);
        var remoteEndpointIds = remoteWindowsPcs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedDeviceIds = plan.Assignments
            .Where(assignment => assignment.EndpointId != "local-pc"
                && !remoteEndpointIds.Contains(assignment.EndpointId))
            .Select(assignment => assignment.EndpointId)
            .ToHashSet(StringComparer.Ordinal);
        var deviceAssignments = plan.Assignments
            .Where(assignment => assignment.EndpointId != "local-pc"
                && !remoteEndpointIds.Contains(assignment.EndpointId))
            .ToArray();
        var remoteAssignments = plan.Assignments
            .Where(assignment => remoteEndpointIds.Contains(assignment.EndpointId))
            .ToArray();
        var localAssignments = plan.Assignments.Where(assignment => assignment.EndpointId == "local-pc").ToArray();
        if (deviceAssignments.Length != plannedDeviceIds.Count || !plannedDeviceIds.SetEquals(devices.Keys))
            throw new InvalidOperationException("The onboarded fleet changed after this multicast plan was generated. Generate a new plan.");
        if (remoteAssignments.Length != remoteEndpointIds.Count
            || !remoteAssignments.Select(assignment => assignment.EndpointId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(remoteEndpointIds))
            throw new InvalidOperationException("The onboarded Windows endpoints changed after this multicast plan was generated. Generate a new plan.");
        if (localAssignments.Length != (plan.IncludeLocalPc ? 1 : 0))
            throw new InvalidOperationException("The local-PC selection changed after this multicast plan was generated. Generate a new plan.");
        var selectedNetwork = plan.IncludeLocalPc
            ? NetworkAddressing.ResolveLocalInterface(
                state.SelectedNetworkAdapterId,
                state.SelectedNetworkAddress)
                ?? throw new InvalidOperationException(
                    "The onboarding network adapter is no longer active. Return to New onboarding and select an active adapter.")
            : null;
        foreach (var assignment in deviceAssignments)
        {
            var device = devices[assignment.EndpointId];
            if (!string.Equals(assignment.Hostname, device.Hostname, StringComparison.Ordinal)
                || !string.Equals(assignment.Address, device.IpAddress, StringComparison.Ordinal)
                || !string.Equals(assignment.Family, device.Family.ToString(), StringComparison.Ordinal)
                || assignment.Role != device.Role
                || assignment.Sender != (device.Role == DeviceRole.Encoder)
                || assignment.Receiver != (device.Role == DeviceRole.Decoder)
                || assignment.Ttl != plan.Ttl)
                throw new InvalidOperationException($"The multicast plan for {device.Hostname} no longer matches the onboarded device. Generate a new plan.");
        }
        foreach (var assignment in remoteAssignments)
        {
            var endpoint = remoteWindowsPcs[assignment.EndpointId];
            if (!string.Equals(assignment.Hostname, endpoint.Hostname, StringComparison.Ordinal)
                || !string.Equals(assignment.Address, endpoint.Address, StringComparison.Ordinal)
                || !string.Equals(assignment.Family, "WindowsPC", StringComparison.Ordinal)
                || assignment.Role != DeviceRole.Encoder
                || !assignment.Sender
                || !assignment.Receiver
                || assignment.Ttl != plan.Ttl)
                throw new InvalidOperationException($"The multicast plan for {endpoint.Hostname} no longer matches the onboarded Windows endpoint. Generate a new plan.");
        }
        if (localAssignments is [{ } local]
            && (!local.Sender
                || !local.Receiver
                || local.Role != DeviceRole.Encoder
                || local.Ttl != plan.Ttl
                || !string.Equals(local.Address, selectedNetwork?.Address, StringComparison.Ordinal)))
            throw new InvalidOperationException("The local-PC multicast assignment is invalid. Generate a new plan.");

        var poolStart = NetworkAddressing.ToUInt(InputValidation.Ip(plan.PoolPrefix, "Multicast pool prefix"));
        var poolMask = NetworkAddressing.ToUInt(InputValidation.Ip(plan.PoolNetmask, "Multicast pool subnet mask"));
        var poolSize = (~poolMask) + 1;
        if (poolSize == 0 || (poolSize & (poolSize - 1)) != 0 || poolMask != ~(poolSize - 1) || (poolStart & poolMask) != poolStart)
            throw new ArgumentException("The multicast pool must use a contiguous subnet mask and an aligned prefix.");
        ValidateAssignments(poolStart, poolSize, plan.Assignments);
        await store.UpdateAsync(current => current with { Multicast = plan with { Status = "running" } });

        var senderAddresses = plan.Assignments
            .Where(assignment => assignment.Sender)
            .Select(assignment => assignment.Address)
            .Where(address => IPAddress.TryParse(address, out _))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var localReceiveSubnets = selectedNetwork is null
            ? []
            : NetworkAddressing.GetSenderSubnets(senderAddresses, selectedNetwork);
        var results = new ConcurrentDictionary<string, MulticastAssignment>(StringComparer.Ordinal);

        async Task ApplyAssignmentAsync(MulticastAssignment assignment, CancellationToken token)
        {
            try
            {
                bool inUse;
                if (assignment.EndpointId == "local-pc")
                {
                    var local = await accessManager.ApplyAsync(
                        assignment.NetPrefix ?? throw new InvalidOperationException("The local PC multicast allocation is missing."),
                        assignment.Netmask ?? throw new InvalidOperationException("The local PC multicast subnet mask is missing."),
                        assignment.Ttl,
                        plan.JobName,
                        job.NdiDiscoveryServerIp,
                        assignment.Address,
                        localReceiveSubnets,
                        token);
                    inUse = local.InUse;
                }
                else if (remoteEndpointIds.Contains(assignment.EndpointId))
                {
                    var agent = pcAgents.Snapshot().FirstOrDefault(candidate =>
                        string.Equals(candidate.EndpointId, assignment.EndpointId, StringComparison.OrdinalIgnoreCase));
                    if (agent is null || !agent.Capabilities.Contains("multicast-config-v1", StringComparer.Ordinal))
                    {
                        results[assignment.EndpointId] = assignment with
                        {
                            Status = "reserved",
                            InUse = false,
                            Error = null
                        };
                        return;
                    }
                    var applied = await pcAgents.ConfigureMulticastAsync(
                        assignment.EndpointId,
                        "multicast",
                        plan.JobName,
                        assignment.NetPrefix,
                        assignment.Netmask,
                        assignment.Ttl,
                        token);
                    inUse = applied.InUse;
                }
                else
                {
                    var device = devices[assignment.EndpointId];
                    await factory.Create(device).ConfigureMulticastAsync(
                        new(plan.JobName, assignment.NetPrefix, assignment.Netmask, assignment.Ttl, senderAddresses),
                        token);
                    inUse = assignment.Receiver
                        || !device.IsTeleTool()
                        || device.StreamRunning == true;
                }
                results[assignment.EndpointId] = assignment with { Status = "applied", InUse = inUse, Error = null };
            }
            catch (Exception ex) when (ex is HttpRequestException
                or TaskCanceledException
                or DeviceApiException
                or InvalidOperationException
                or IOException
                or KeyNotFoundException
                or System.Text.Json.JsonException
                or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Multicast configuration failed for {EndpointId}", assignment.EndpointId);
                results[assignment.EndpointId] = assignment with { Status = "error", InUse = false, Error = ex.Message };
            }
        }

        // Senders must advertise their multicast transport before a decoder is
        // configured or it can cache the same source as unicast and retain that
        // transport in its preset bank.
        await Parallel.ForEachAsync(
            plan.Assignments.Where(assignment => assignment.Sender),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (assignment, token) => await ApplyAssignmentAsync(assignment, token));
        await Parallel.ForEachAsync(
            plan.Assignments.Where(assignment => !assignment.Sender),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (assignment, token) => await ApplyAssignmentAsync(assignment, token));

        var encoderDevices = devices.Values
            .Where(device => device.Role == DeviceRole.Encoder)
            .OrderBy(device => device.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failedEncoderNames = encoderDevices
            .Where(device => !results.TryGetValue(device.Id, out var assignment) || assignment.Status != "applied")
            .Select(device => device.Hostname)
            .ToArray();
        var configuredEncoders = failedEncoderNames.Length == 0
            ? encoderDevices.Select(device =>
            {
                var assignment = results[device.Id];
                return device with
                {
                    MulticastConfigured = true,
                    MulticastInUse = assignment.InUse,
                    MulticastNetPrefix = assignment.NetPrefix,
                    MulticastNetmask = assignment.Netmask,
                    MulticastTtl = assignment.Ttl,
                    MulticastLastError = null
                };
            }).ToArray()
            : [];
        await Parallel.ForEachAsync(
            plan.Assignments.Where(assignment => assignment.Receiver && !assignment.Sender),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (assignment, token) =>
            {
                if (!results.TryGetValue(assignment.EndpointId, out var decoderResult)
                    || decoderResult.Status != "applied") return;
                try
                {
                    if (failedEncoderNames.Length > 0)
                        throw new InvalidOperationException(
                            $"Decoder presets were not refreshed because multicast failed on: {string.Join(", ", failedEncoderNames)}.");
                    var decoder = devices[assignment.EndpointId];
                    await factory.Create(decoder).ConfigureDecoderFeedsAsync(configuredEncoders, token);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or TaskCanceledException
                    or DeviceApiException
                    or InvalidOperationException
                    or IOException
                    or KeyNotFoundException
                    or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Refreshing multicast decoder presets failed for {EndpointId}", assignment.EndpointId);
                    results[assignment.EndpointId] = decoderResult with
                    {
                        Status = "error",
                        InUse = false,
                        Error = $"Multicast receive mode was applied, but decoder presets could not be refreshed: {ex.Message}"
                    };
                }
            });

        if (results.TryGetValue("local-pc", out var localResult) && localResult.Status == "applied")
        {
            try
            {
                await thumbnails.ReloadNdiConfigurationAsync(ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TaskCanceledException)
            {
                logger.LogWarning(ex, "The local multicast configuration was applied, but the encoder preview runtime could not be reloaded");
                results["local-pc"] = localResult with
                {
                    Status = "error",
                    InUse = false,
                    Error = $"NDI Access Manager was updated, but the in-app preview receiver could not reload: {ex.Message}"
                };
            }
        }

        var completedAssignments = plan.Assignments.Select(assignment => results.TryGetValue(assignment.EndpointId, out var result)
            ? result
            : assignment with { Status = "error", Error = "The multicast configuration did not complete." }).ToArray();
        var failed = completedAssignments.Count(assignment => assignment.Status == "error");
        var completed = plan with
        {
            Assignments = completedAssignments,
            Status = failed == 0 ? "completed" : "partial",
            AppliedUtc = DateTimeOffset.UtcNow
        };

        await store.UpdateAsync(current => current with
        {
            Multicast = completed,
            Devices = current.Devices.Select(device =>
            {
                var assignment = completedAssignments.FirstOrDefault(candidate => candidate.EndpointId == device.Id);
                if (assignment is null) return device;
                return device with
                {
                    MulticastConfigured = assignment.Status == "applied",
                    MulticastInUse = assignment.InUse,
                    MulticastNetPrefix = assignment.NetPrefix,
                    MulticastNetmask = assignment.Netmask,
                    MulticastTtl = assignment.Ttl,
                    MulticastLastError = assignment.Error
                };
            }).ToArray()
        });

        var applied = completedAssignments.Count(assignment => assignment.Status == "applied");
        return new(completed.Status, applied, failed, completed);
    }

    public async Task<MulticastRevertResult> RevertToUnicastAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var current = state.Multicast
            ?? throw new InvalidOperationException("Multicast is not currently configured for this job.");
        var devices = state.Devices
            .Where(device => device.IsOnboarded)
            .ToDictionary(device => device.Id, StringComparer.Ordinal);
        var includeLocalPc = current.IncludeLocalPc
            || current.Assignments.Any(assignment => assignment.EndpointId == "local-pc");
        var remoteWindowsPcs = (state.RemoteWindowsPcs ?? [])
            .ToDictionary(endpoint => endpoint.EndpointId, StringComparer.OrdinalIgnoreCase);
        var remoteEndpointIds = remoteWindowsPcs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteAssignments = current.Assignments
            .Where(assignment => remoteEndpointIds.Contains(assignment.EndpointId))
            .ToArray();
        if (devices.Count == 0 && remoteAssignments.Length == 0 && !includeLocalPc)
            throw new InvalidOperationException("There are no multicast endpoints to revert.");

        await store.UpdateAsync(app => app with
        {
            Multicast = app.Multicast is null ? null : app.Multicast with { Status = "reverting" }
        });

        var results = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            remoteAssignments,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (assignment, token) =>
            {
                try
                {
                    var agent = pcAgents.Snapshot().FirstOrDefault(candidate =>
                        string.Equals(candidate.EndpointId, assignment.EndpointId, StringComparison.OrdinalIgnoreCase));
                    if (agent is null || !agent.Capabilities.Contains("multicast-config-v1", StringComparer.Ordinal))
                        throw new NotSupportedException(
                            "This PC Agent cannot revert NDI Access Manager remotely. Disable multicast manually or update the agent.");
                    await pcAgents.ConfigureMulticastAsync(
                        assignment.EndpointId,
                        "unicast",
                        current.JobName,
                        null,
                        null,
                        null,
                        token);
                    results[assignment.EndpointId] = null;
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or TaskCanceledException
                    or InvalidOperationException
                    or IOException
                    or KeyNotFoundException
                    or NotSupportedException
                    or System.Text.Json.JsonException
                    or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Reverting remote Windows multicast failed for {EndpointId}", assignment.EndpointId);
                    results[assignment.EndpointId] = ex.Message;
                }
            });
        await Parallel.ForEachAsync(
            devices.Values,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (device, token) =>
            {
                try
                {
                    await factory.Create(device).DisableMulticastAsync(token);
                    results[device.Id] = null;
                }
                catch (Exception ex) when (ex is HttpRequestException
                    or TaskCanceledException
                    or DeviceApiException
                    or InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Reverting multicast failed for {EndpointId}", device.Id);
                    results[device.Id] = ex.Message;
                }
            });

        if (includeLocalPc)
        {
            try
            {
                await accessManager.DisableMulticastAsync(ct);
                await thumbnails.ReloadNdiConfigurationAsync(ct);
                results["local-pc"] = null;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or IOException
                or TaskCanceledException
                or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Reverting local NDI Access Manager multicast settings failed");
                results["local-pc"] = ex.Message;
            }
        }

        var failedResults = results
            .Where(result => result.Value is not null)
            .ToDictionary(result => result.Key, result => result.Value!, StringComparer.Ordinal);
        var failed = failedResults.Count;
        var reverted = results.Count - failed;
        if (failed == 0)
        {
            await store.UpdateAsync(app => app with
            {
                Multicast = null,
                Devices = app.Devices.Select(ClearMulticast).ToArray()
            });
            return new("completed", reverted, 0, null, []);
        }

        var assignments = current.Assignments.Select(assignment =>
        {
            if (!results.TryGetValue(assignment.EndpointId, out var error)) return assignment;
            return error is null
                ? assignment with { Status = "unicast", InUse = false, Error = null }
                : assignment with { Status = "error", InUse = false, Error = error };
        }).ToArray();
        var partial = current with
        {
            Status = "revert-partial",
            Assignments = assignments,
            AppliedUtc = DateTimeOffset.UtcNow
        };
        await store.UpdateAsync(app => app with
        {
            Multicast = partial,
            Devices = app.Devices.Select(device =>
            {
                if (!results.TryGetValue(device.Id, out var error)) return device;
                return error is null
                    ? ClearMulticast(device)
                    : device with { MulticastInUse = false, MulticastLastError = error };
            }).ToArray()
        });
        var errors = failedResults.Select(result =>
        {
            var name = result.Key == "local-pc"
                ? Environment.MachineName
                : devices.TryGetValue(result.Key, out var device) ? device.Hostname : result.Key;
            return $"{name}: {result.Value}";
        }).ToArray();
        return new("partial", reverted, failed, partial, errors);
    }

    private static ManagedDevice ClearMulticast(ManagedDevice device) => device with
    {
        MulticastConfigured = false,
        MulticastInUse = false,
        MulticastNetPrefix = null,
        MulticastNetmask = null,
        MulticastTtl = null,
        MulticastLastError = null
    };

    private static int PoolPrefixLength(int slots)
    {
        // Keep one readable /16 per job and reserve third-octet zero. Sender
        // allocations then progress as 239.x.1.0/24, 239.x.2.0/24, and so on.
        if (slots > 255)
            throw new InvalidOperationException("A multicast job supports up to 255 sender endpoints.");
        return 16;
    }

    private static string SenderPrefix(uint poolStart, uint zeroBasedSlot) =>
        NetworkAddressing.FromUInt(poolStart + (zeroBasedSlot + 1) * AllocationSize).ToString();

    private static uint SelectPool(string jobName, uint poolSize, bool regenerate, MulticastConfiguration? previous)
    {
        // A range-layout upgrade must not move an existing job to a different
        // organization-local /16 merely because its old pool was smaller.
        // Preserve the first two octets and replace only the sender blocks with
        // the new readable .1, .2, .3 ... /24 sequence.
        if (!regenerate
            && previous is not null
            && string.Equals(previous.JobName, jobName, StringComparison.Ordinal)
            && IPAddress.TryParse(previous.PoolPrefix, out var previousPrefix))
        {
            var alignedPrevious = NetworkAddressing.ToUInt(previousPrefix) & PrefixMask(16);
            if (alignedPrevious >= ScopeStart && alignedPrevious + poolSize - 1 <= ScopeEnd)
                return alignedPrevious;
        }

        var scopeSize = ScopeEnd - ScopeStart + 1;
        var blockCount = scopeSize / poolSize;
        uint selected;
        if (regenerate)
        {
            selected = (uint)RandomNumberGenerator.GetInt32((int)blockCount);
        }
        else
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(jobName.Trim().ToUpperInvariant()));
            selected = BitConverter.ToUInt32(hash, 0) % blockCount;
        }

        var candidate = ScopeStart + selected * poolSize;
        if (previous is not null
            && !string.Equals(previous.JobName, jobName, StringComparison.Ordinal)
            && RangesOverlap(
                candidate,
                candidate + poolSize - 1,
                NetworkAddressing.ToUInt(IPAddress.Parse(previous.PoolPrefix)),
                NetworkAddressing.ToUInt(IPAddress.Parse(previous.PoolLastAddress))))
            candidate = ScopeStart + ((selected + 1) % blockCount) * poolSize;
        return candidate;
    }

    private static void ValidateAssignments(uint poolStart, uint poolSize, IReadOnlyList<MulticastAssignment> assignments)
    {
        var poolEnd = poolStart + poolSize - 1;
        if (poolStart < ScopeStart || poolEnd > ScopeEnd)
            throw new ArgumentException("The generated multicast pool must remain inside the organization-local 239.192.0.0/14 range.");

        var ranges = new List<(uint Start, uint End, string Id)>();
        foreach (var assignment in assignments.Where(candidate => candidate.Sender))
        {
            var prefix = NetworkAddressing.ToUInt(InputValidation.Ip(
                assignment.NetPrefix ?? throw new ArgumentException($"Sender {assignment.Hostname} is missing its multicast prefix."),
                $"Multicast prefix for {assignment.Hostname}"));
            var mask = NetworkAddressing.ToUInt(InputValidation.Ip(
                assignment.Netmask ?? throw new ArgumentException($"Sender {assignment.Hostname} is missing its multicast subnet mask."),
                $"Multicast subnet mask for {assignment.Hostname}"));
            if (mask != PrefixMask(AllocationPrefixLength) || (prefix & mask) != prefix)
                throw new ArgumentException($"Sender {assignment.Hostname} must use an aligned /{AllocationPrefixLength} multicast allocation.");
            var end = prefix + AllocationSize - 1;
            if (prefix < poolStart || end > poolEnd)
                throw new ArgumentException($"Sender {assignment.Hostname}'s multicast allocation is outside the generated pool.");
            if (ranges.Any(existing => RangesOverlap(prefix, end, existing.Start, existing.End)))
                throw new ArgumentException($"Sender {assignment.Hostname}'s multicast allocation conflicts with another endpoint.");
            ranges.Add((prefix, end, assignment.EndpointId));
        }
    }

    private static uint PrefixMask(int prefixLength) => prefixLength == 0
        ? 0
        : uint.MaxValue << (32 - prefixLength);

    private static bool RangesOverlap(uint firstStart, uint firstEnd, uint secondStart, uint secondEnd) =>
        firstStart <= secondEnd && secondStart <= firstEnd;

}
