using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using KiloviewSetup.Devices;

namespace KiloviewSetup.Core;

public sealed class OnboardingService(
    AppStateStore store,
    DeviceClientFactory factory,
    KiloLinkCredentialStore credentialStore,
    KiloLinkServerClient kiloLink,
    NdiTitleCardService titleCards,
    NdiAccessManagerService accessManager,
    EncoderThumbnailService thumbnails,
    ILogger<OnboardingService> logger)
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(15);
    // Size the pipeline for the supported 1 Gb/s network floor. Firmware files
    // are hundreds of megabytes, so keep two uploads in flight and leave
    // capacity for device control, Discovery Server, KiloLink, and live NDI.
    private const int DevicePipelineConcurrency = 8;
    private const int DisruptiveOperationConcurrency = 4;
    private const int FirmwareUploadConcurrency = 2;
    private const int DecoderPresetConcurrency = 6;
    private readonly object _progressGate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<Guid, OnboardingPlan> _plans = [];
    private OnboardingProgress _progress = new(Guid.Empty, "idle", 0, 0, [], DateTimeOffset.UtcNow);
    private Task? _run;
    public OnboardingProgress Progress { get { lock (_progressGate) return _progress; } }

    public async Task<OnboardingPlan> BuildPlanAsync(OnboardingRequest request, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var selected = request.DeviceIds.Distinct().Select(id => state.Devices.FirstOrDefault(d => d.Id == id)
            ?? throw new ArgumentException($"Selected device '{id}' is no longer in the discovery list.")).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Select at least one device to onboard.");
        if (selected.Any(d => !d.CanOnboard))
            throw new ArgumentException(selected.First(d => !d.CanOnboard).ManagementMessage ?? "One or more selected devices cannot be onboarded.");
        var requiresKiloLink = selected.Any(d => d.IsKiloview() && !d.IsSimulation());
        InputValidation.Validate(request, requiresKiloLink);
        if (requiresKiloLink)
        {
            var serverCredential = credentialStore.ResolveAndStore(request.KiloLinkServerIp, request.KiloLinkUsername, request.KiloLinkPassword);
            request = request with { KiloLinkUsername = serverCredential.Username, KiloLinkPassword = "" };
        }
        else request = request with { KiloLinkPassword = "" };

        var range = NetworkAddressing.Range(request.StaticStart, request.StaticEnd).Select(x => x.ToString()).ToArray();
        var occupied = new ConcurrentDictionary<string, byte>();
        foreach (var device in state.Devices.Where(d => d.IsStatic || d.IsOnboarded))
            if (range.Contains(device.IpAddress)) occupied.TryAdd(device.IpAddress, 0);

        await Parallel.ForEachAsync(range, new ParallelOptions
        {
            MaxDegreeOfParallelism = NetworkAddressing.DiscoveryParallelism(range.Length),
            CancellationToken = ct
        }, async (address, token) =>
        {
            if (occupied.ContainsKey(address)) return;
            if (await AddressRespondsAsync(address, token)) occupied.TryAdd(address, 0);
        });

        var startNumber = NetworkAddressing.ToUInt(IPAddress.Parse(request.StaticStart));
        var next = startNumber;
        var end = NetworkAddressing.ToUInt(IPAddress.Parse(request.StaticEnd));
        var plans = new List<DevicePlan>();
        var kiloviewNumber = 0;
        var teleToolNumber = 0;
        foreach (var device in selected)
        {
            var hostname = device.IsTeleTool()
                ? $"{SanitizeName(request.JobName)}-TT-{++teleToolNumber:000}"
                : $"{SanitizeName(request.JobName)}-KV-{++kiloviewNumber:000}";
            var role = device.IsTeleTool()
                ? DeviceRole.Encoder
                : request.RoleOverrides is not null && request.RoleOverrides.TryGetValue(device.Id, out var value) ? value : DeviceRole.Unknown;
            var existingStatic = range.Contains(device.IpAddress) && device.IsStatic;
            if (existingStatic)
            {
                plans.Add(new(device.Id, device.IpAddress, device.IpAddress, hostname, role, true, device.Family));
                continue;
            }
            while (next <= end && occupied.ContainsKey(NetworkAddressing.FromUInt(next).ToString())) next++;
            if (next > end) throw new ArgumentException("There are not enough unused addresses in the static range.");
            var target = NetworkAddressing.FromUInt(next++).ToString();
            plans.Add(new(device.Id, device.IpAddress, target, hostname, role, false, device.Family));
            occupied.TryAdd(target, 0);
        }

        var warnings = new List<string>();
        if (selected.Any(d => d.IsKiloview()))
        {
            warnings.Add($"Confirming authorizes the application to accept the Kiloview EULA on each selected Kiloview and set its device login to admin / {request.JobName}.");
            warnings.Add("Model-specific firmware is staged before confirmation. Outdated Kiloviews are upgraded and verified at their current address before static IP or KiloLink changes begin.");
            warnings.Add("KiloLink authorization codes will be generated for Kiloview units; each KiloLink alias will match the assigned hostname.");
            warnings.Add("Kiloview role detection checks the live HDMI input while each unit is in encoder mode. Units with an active input remain encoders; units with no input switch to decoder mode so they can show display-identification cards.");
            warnings.Add("Keep intended HDMI input sources powered on until role detection completes. Decoder assignment means no encoder input was detected; it does not claim that HDMI output hot-plug was electrically verified.");
        }
        if (request.CleanOnboarding)
            warnings.Add("CLEAN ONBOARDING: confirming permanently deletes every existing device and real device group from KiloLink Server and clears prior configurator job devices before this plan starts.");
        if (selected.Any(d => d.IsTeleTool()))
        {
            warnings.Add($"TeleTools will remain encoders and receive hostnames, NDI channel names, Discovery Server {request.NdiDiscoveryServerIp}, and NDI group '{request.JobName}' from the TeleTool Dev API.");
            warnings.Add("TeleTools already adopted by another Fleet Manager or managing their own fleet cannot be selected.");
        }
        var previousLocalGroup = state.ManagedLocalNdiGroup ?? state.LastJob?.JobName;
        warnings.Add(string.IsNullOrWhiteSpace(previousLocalGroup)
            ? $"The local PC will use '{request.JobName}' as its NDI send and receive group."
            : string.Equals(previousLocalGroup, request.JobName, StringComparison.OrdinalIgnoreCase)
                ? $"The local PC NDI send and receive group will remain '{request.JobName}'."
                : $"The local PC NDI group '{previousLocalGroup}' will be replaced by '{request.JobName}' in send and receive, while unrelated groups are preserved.");
        var plan = new OnboardingPlan(
            Guid.NewGuid(),
            request,
            plans,
            occupied.Keys.OrderBy(x => NetworkAddressing.ToUInt(IPAddress.Parse(x))).ToArray(),
            warnings,
            DateTimeOffset.UtcNow.Add(PlanLifetime));
        lock (_progressGate)
        {
            foreach (var expired in _plans.Where(candidate => candidate.Value.ExpiresUtc <= DateTimeOffset.UtcNow).Select(candidate => candidate.Key).ToArray())
                _plans.Remove(expired);
            _plans[plan.PlanId] = plan;
        }
        return plan;
    }

    public async Task<object> StartAsync(Guid planId, CancellationToken ct)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            OnboardingPlan plan;
            lock (_progressGate)
            {
                if (_run is { IsCompleted: false }) throw new InvalidOperationException("An onboarding run is already in progress.");
                if (!_plans.Remove(planId, out plan!))
                    throw new InvalidOperationException("This onboarding plan is missing, expired, or has already been used. Generate a new plan.");
            }
            if (plan.ExpiresUtc <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("This onboarding plan expired. Generate a new plan.");
            await ValidatePlanAsync(plan, ct);
            await ValidateFirmwareCoverageAsync(plan);
            if (plan.Settings.CleanOnboarding)
                await PrepareCleanOnboardingAsync(plan, ct);
            await ApplyLocalNdiJobAsync(plan.Settings, ct);
            await store.UpdateAsync(state => state.FirmwareJob is null ? state : state with
            {
                FirmwareJob = state.FirmwareJob with { Status = "running", FinishedUtc = null, Message = "Applying model firmware before network and KiloLink configuration." }
            });
            lock (_progressGate)
            {
                if (_run is { IsCompleted: false }) throw new InvalidOperationException("An onboarding run is already in progress.");
                titleCards.StopAll();
                var total = Math.Max(1, plan.Devices.Sum(device => device.Family is DeviceFamily.TeleTool or DeviceFamily.SimulatedTeleTool ? 5 : 10));
                _progress = new(Guid.NewGuid(), "running", 0, total, [], DateTimeOffset.UtcNow);
                _run = Task.Run(() => ExecuteAsync(plan));
                return new { _progress.RunId, _progress.Status };
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task ApplyLocalNdiJobAsync(OnboardingRequest settings, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var selected = NetworkAddressing.ResolveLocalInterface(
            state.SelectedNetworkAdapterId,
            state.SelectedNetworkAddress)
            ?? throw new InvalidOperationException(
                "The onboarding network adapter is no longer active. Return to New onboarding and select an active adapter.");
        var previousGroup = state.ManagedLocalNdiGroup ?? state.LastJob?.JobName;
        await accessManager.ApplyJobGroupAsync(
            settings.JobName,
            previousGroup,
            settings.NdiDiscoveryServerIp,
            selected.Address,
            ct);
        try
        {
            await thumbnails.ReloadNdiConfigurationAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            logger.LogWarning(ex, "The local NDI job group was applied, but the preview receiver could not reload");
        }

        await store.UpdateAsync(current => current with
        {
            ManagedLocalNdiGroup = settings.JobName,
            LocalPc = current.LocalPc is null
                ? null
                : current.LocalPc with
                {
                    Status = "applied",
                    Error = null
                }
        });
    }

    private async Task ValidatePlanAsync(OnboardingPlan plan, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var requiresKiloLink = plan.Devices.Any(item => item.Family is DeviceFamily.N6 or DeviceFamily.N60);
        InputValidation.Validate(plan.Settings, requiresKiloLink);
        var range = NetworkAddressing.Range(plan.Settings.StaticStart, plan.Settings.StaticEnd)
            .Select(address => address.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var targetAddresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plan.Devices)
        {
            var device = state.Devices.FirstOrDefault(candidate => candidate.Id == item.DeviceId)
                ?? throw new InvalidOperationException($"Device '{item.DeviceId}' is no longer available. Generate a new plan.");
            if (!device.CanOnboard)
                throw new InvalidOperationException(device.ManagementMessage ?? $"Device '{device.Hostname}' can no longer be onboarded.");
            if (device.Family != item.Family || !string.Equals(device.IpAddress, item.CurrentIp, StringComparison.Ordinal))
                throw new InvalidOperationException($"Device '{device.Hostname}' changed after the plan was generated. Generate a new plan.");
            if (item.ExistingStaticDevice)
            {
                if (!device.IsStatic || !range.Contains(device.IpAddress) ||
                    !string.Equals(device.IpAddress, item.TargetIp, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Device '{device.Hostname}' no longer matches its retained static address. Generate a new plan.");
                continue;
            }
            if (range.Contains(device.IpAddress))
                throw new InvalidOperationException($"Device '{device.Hostname}' is already inside the static range and will not be changed.");
            if (!range.Contains(item.TargetIp) || !targetAddresses.Add(item.TargetIp))
                throw new InvalidOperationException("The onboarding address plan is no longer valid. Generate a new plan.");
        }

        var selectedIds = plan.Devices.Select(item => item.DeviceId).ToHashSet(StringComparer.Ordinal);
        var newlyOccupied = state.Devices
            .Where(device => !selectedIds.Contains(device.Id) && targetAddresses.Contains(device.IpAddress))
            .Select(device => device.IpAddress)
            .ToArray();
        if (newlyOccupied.Length > 0)
            throw new InvalidOperationException($"A planned address is now in use ({string.Join(", ", newlyOccupied)}). Generate a new plan.");

        var responsiveTargets = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(targetAddresses, new ParallelOptions
        {
            MaxDegreeOfParallelism = NetworkAddressing.DiscoveryParallelism(targetAddresses.Count),
            CancellationToken = ct
        }, async (address, token) =>
        {
            if (await AddressRespondsAsync(address, token)) responsiveTargets.Add(address);
        });
        if (!responsiveTargets.IsEmpty)
            throw new InvalidOperationException($"A planned address is now responding ({string.Join(", ", responsiveTargets.Order())}). Generate a new plan.");
    }

    private async Task ExecuteAsync(OnboardingPlan plan)
    {
        var ready = new ConcurrentBag<ManagedDevice>();
        using var disruptiveOperations = new SemaphoreSlim(DisruptiveOperationConcurrency, DisruptiveOperationConcurrency);
        using var firmwareUploads = new SemaphoreSlim(FirmwareUploadConcurrency, FirmwareUploadConcurrency);
        KiloLinkCredential? serverCredential = null;
        try
        {
            if (plan.Devices.Any(item => item.Family is DeviceFamily.N6 or DeviceFamily.N60))
            {
                serverCredential = credentialStore.ResolveAndStore(
                    plan.Settings.KiloLinkServerIp,
                    plan.Settings.KiloLinkUsername,
                    plan.Settings.KiloLinkPassword);
            }
            var pipelineOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(DevicePipelineConcurrency, Math.Max(1, plan.Devices.Count))
            };
            var roleOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(DevicePipelineConcurrency, Math.Max(1, plan.Devices.Count))
            };
            var presetOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(DecoderPresetConcurrency, Math.Max(1, plan.Devices.Count))
            };
            logger.LogInformation(
                "Starting onboarding for {DeviceCount} devices with pipeline concurrency {PipelineConcurrency}, disruptive-operation concurrency {DisruptiveConcurrency}, firmware concurrency {FirmwareConcurrency}, and decoder-preset concurrency {PresetConcurrency}",
                plan.Devices.Count,
                pipelineOptions.MaxDegreeOfParallelism,
                DisruptiveOperationConcurrency,
                FirmwareUploadConcurrency,
                presetOptions.MaxDegreeOfParallelism);

            await Parallel.ForEachAsync(plan.Devices, pipelineOptions, async (item, _) =>
            {
                var device = (await store.ReadAsync()).Devices.First(d => d.Id == item.DeviceId);
                try
                {
                    if (device.IsTeleTool())
                    {
                        await disruptiveOperations.WaitAsync(CancellationToken.None);
                        try
                        {
                            await OnboardTeleToolAsync(device, item, plan.Settings);
                        }
                        finally
                        {
                            disruptiveOperations.Release();
                        }
                        return;
                    }

                    var targetCredentials = new DeviceCredentials("admin", plan.Settings.JobName);
                    Step(device, "Access & license", "running", "Accepting EULA and applying job credentials");
                    var discoveredDeviceId = device.Id;
                    await disruptiveOperations.WaitAsync(CancellationToken.None);
                    try
                    {
                        await factory.Create(device).ProvisionAccessAsync(targetCredentials, CancellationToken.None);
                        var verified = await factory.Create(device with { Credentials = targetCredentials }).ReadAsync(CancellationToken.None);
                        device = verified with
                        {
                            Credentials = targetCredentials,
                            LicenseAccepted = true,
                            Health = DeviceHealth.Configuring
                        };
                    }
                    finally
                    {
                        disruptiveOperations.Release();
                    }
                    CompleteStep(device, "Access & license", "EULA accepted; admin password set to Job Name");
                    await ReplaceDeviceAsync(discoveredDeviceId, device);

                    Step(device, "Firmware", "running", "Checking staged model firmware before network changes");
                    try
                    {
                        await firmwareUploads.WaitAsync(CancellationToken.None);
                        try
                        {
                            await disruptiveOperations.WaitAsync(CancellationToken.None);
                            try
                            {
                                device = await ApplyFirmwareBeforeConfigurationAsync(device);
                            }
                            finally
                            {
                                disruptiveOperations.Release();
                            }
                        }
                        finally
                        {
                            firmwareUploads.Release();
                        }
                        CompleteStep(device, "Firmware", $"Verified {device.FirmwareVersion}");
                    }
                    catch (Exception ex)
                    {
                        FailStep(device, "Firmware", ex.Message);
                        throw;
                    }

                    Step(device, "Static IP & DNS", "running", item.ExistingStaticDevice
                        ? $"Retaining existing network settings at {item.TargetIp}"
                        : $"Assigning {item.TargetIp}; DNS {plan.Settings.Dns}");
                    if (!item.ExistingStaticDevice)
                    {
                        await disruptiveOperations.WaitAsync(CancellationToken.None);
                        try
                        {
                            await factory.Create(device).SetNetworkAsync(
                                item.TargetIp,
                                plan.Settings.SubnetMask,
                                plan.Settings.Gateway,
                                plan.Settings.Dns,
                                CancellationToken.None);
                            device = device with { IpAddress = item.TargetIp, IsStatic = true, Health = DeviceHealth.Configuring };
                            await SaveDeviceAsync(device);
                            CompleteStep(device, "Static IP & DNS", $"Address assigned; DNS {plan.Settings.Dns} applied");

                            Step(device, "Reconnect", "running");
                            device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.IsKiloview() ? 90 : 45));
                        }
                        finally
                        {
                            disruptiveOperations.Release();
                        }
                    }
                    else
                    {
                        device = device with { IpAddress = item.TargetIp, IsStatic = true, Health = DeviceHealth.Configuring };
                        await SaveDeviceAsync(device);
                        CompleteStep(device, "Static IP & DNS", "Already in static range; network settings unchanged");

                        Step(device, "Reconnect", "running");
                        device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.IsKiloview() ? 90 : 45));
                    }
                    CompleteStep(device, "Reconnect", "Device reachable on static IP");

                    var requestedRole = item.Role;
                    var hasRoleOverride = requestedRole != DeviceRole.Unknown;

                    // Kiloview identity and HDMI-input APIs are encoder services.
                    // Even an explicitly selected decoder must pass through a
                    // healthy encoder phase before its final role is applied.
                    var preparationRole = DeviceRole.Encoder;
                    if (device.Role != preparationRole)
                    {
                        Step(device, "Prepare", "running", hasRoleOverride
                            ? $"Preparing encoder services before applying explicit {requestedRole} role"
                            : "Ensuring encoder mode is ready for NDI setup and input detection");
                        logger.LogInformation(
                            "Changing {Model} {DeviceId} at {Address} from {CurrentRole} to {TargetRole} during onboarding preparation",
                            device.Model,
                            device.Id,
                            device.IpAddress,
                            device.Role,
                            preparationRole);
                        await disruptiveOperations.WaitAsync(CancellationToken.None);
                        try
                        {
                            await factory.Create(device).SetRoleAsync(preparationRole, CancellationToken.None);
                            device = device with { Role = preparationRole };
                            device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.IsKiloview() ? 90 : 45));
                        }
                        finally
                        {
                            disruptiveOperations.Release();
                        }
                        CompleteStep(device, "Prepare", $"{preparationRole} mode ready");
                    }
                    else CompleteStep(device, "Prepare", $"{device.Role} mode ready");

                    Step(device, "KiloLink authorization", "running", "Generating server-side device code");
                    var authorizationCode = device.IsSimulation()
                        ? $"SIM-{device.Id}"
                        : (await kiloLink.AuthorizeDeviceAsync(
                            plan.Settings.KiloLinkServerIp,
                            plan.Settings.KiloLinkWebPort,
                            serverCredential!,
                            device.Id,
                            item.Hostname,
                            CancellationToken.None)).AuthorizationCode;
                    CompleteStep(device, "KiloLink authorization", "Serial registered; alias matches hostname");

                    Step(device, "KiloLink / NDI", "running");
                    var deviceSettings = plan.Settings with { KiloLinkOnboardingCode = authorizationCode };
                    await factory.Create(device).ConfigureOnboardingAsync(deviceSettings, item.Hostname, $"{SanitizeName(plan.Settings.JobName)}-{item.TargetIp.Split('.').Last()}", CancellationToken.None);
                    device = device with
                    {
                        Hostname = item.Hostname,
                        NdiChannelName = $"{SanitizeName(plan.Settings.JobName)}-{item.TargetIp.Split('.').Last()}",
                        NdiGroup = plan.Settings.JobName,
                        IsOnboarded = true,
                        Health = DeviceHealth.Online,
                        LastError = null
                    };
                    await SaveDeviceAsync(device);
                    CompleteStep(device, "KiloLink / NDI", device.Family == DeviceFamily.N60
                        ? $"NDI-HB (no record) selected; NDI-HX disabled; group '{plan.Settings.JobName}' applied from Job Name"
                        : $"NDI group '{plan.Settings.JobName}' applied from Job Name");
                    ready.Add(device);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to onboard {Device}", item.DeviceId);
                    await SaveDeviceAsync(device with { Health = DeviceHealth.Error, LastError = ex.Message });
                    FailStep(device, "Onboarding", ex.Message);
                }
            });

            await Parallel.ForEachAsync(ready, roleOptions, async (original, _) =>
            {
                var device = (await store.ReadAsync()).Devices.FirstOrDefault(d => d.Id == original.Id) ?? original;
                var finalizationStep = "HDMI role detection";
                try
                {
                    var overrideRole = DeviceRole.Unknown;
                    var forced = plan.Settings.RoleOverrides is not null && plan.Settings.RoleOverrides.TryGetValue(device.Id, out overrideRole) && overrideRole != DeviceRole.Unknown;
                    Step(device, "HDMI role detection", "running", forced
                        ? $"Applying explicit {overrideRole} role"
                        : "Checking for a live HDMI input in encoder mode");

                    var input = forced
                        ? new HdmiInputProbeResult(false, null)
                        : await factory.Create(device).ProbeEncoderInputAsync(CancellationToken.None);
                    var role = forced
                        ? overrideRole
                        : input.SignalPresent ? DeviceRole.Encoder : DeviceRole.Decoder;

                    if (role == DeviceRole.Decoder && device.Role != DeviceRole.Decoder)
                    {
                        logger.LogInformation(
                            "Changing {Model} {DeviceId} at {Address} from {CurrentRole} to Decoder after HDMI role detection",
                            device.Model,
                            device.Id,
                            device.IpAddress,
                            device.Role);
                        await disruptiveOperations.WaitAsync(CancellationToken.None);
                        try
                        {
                            await factory.Create(device).SetRoleAsync(DeviceRole.Decoder, CancellationToken.None);
                            device = device with { Role = DeviceRole.Decoder, Health = DeviceHealth.Configuring };
                            device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 90 : 60));
                        }
                        finally
                        {
                            disruptiveOperations.Release();
                        }
                    }
                    device = device with
                    {
                        Role = role,
                        HdmiDisplayConnected = null,
                        HdmiOutputResolution = null,
                        Health = DeviceHealth.Online,
                        LastError = null,
                        ManagementState = null,
                        ManagementMessage = null
                    };
                    await SaveDeviceAsync(device);
                    CompleteStep(device, "HDMI role detection", forced
                        ? $"Role set explicitly to {role}"
                        : input.SignalPresent
                            ? $"Encoder — live HDMI input{(string.IsNullOrWhiteSpace(input.Resolution) ? "" : $" at {input.Resolution}")}"
                            : "Decoder — no live encoder input; ready for display identification");

                    finalizationStep = "NDI Discovery Server";
                    Step(device, finalizationStep, "running", $"Applying {plan.Settings.NdiDiscoveryServerIp} after final role selection");
                    await factory.Create(device).ConfigureDiscoveryServerAsync(
                        plan.Settings.NdiDiscoveryServerIp,
                        plan.Settings.JobName,
                        CancellationToken.None);
                    CompleteStep(device, "NDI Discovery Server", $"Connected to {plan.Settings.NdiDiscoveryServerIp} for group '{plan.Settings.JobName}'");

                    // Identity is persisted now; decoder cards can fine-tune both names on the next UI page.
                    CompleteStep(device, "Identity", device.Hostname);
                }
                catch (Exception ex)
                {
                    await SaveDeviceAsync(device with { Health = DeviceHealth.Error, LastError = ex.Message });
                    FailStep(device, finalizationStep, ex.Message);
                }
            });

            var onboardedFleet = (await store.ReadAsync()).Devices
                .Where(device => device.IsOnboarded &&
                                 device.Health != DeviceHealth.Error &&
                                 string.Equals(device.NdiGroup, plan.Settings.JobName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var stoppedTeleTools = onboardedFleet.Count(device =>
                device.Role == DeviceRole.Encoder && device.IsTeleTool() && device.StreamRunning == false);
            var encoders = onboardedFleet
                .Where(device => device.Role == DeviceRole.Encoder &&
                                 (!device.IsTeleTool() || device.StreamRunning != false))
                .OrderBy(device => device.Hostname, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.IpAddress, StringComparer.Ordinal)
                .ToArray();
            var decoders = onboardedFleet
                .Where(device => device.Role == DeviceRole.Decoder && device.IsKiloview())
                .ToArray();
            AddProgressWork(decoders.Length);
            await Parallel.ForEachAsync(decoders, presetOptions, async (decoder, _) =>
            {
                try
                {
                    Step(decoder, "Decoder feed presets", "running", encoders.Length == 0
                        ? "Clearing stale feed presets; this job has no encoders"
                        : $"Adding {encoders.Length} advertising job encoder feed{(encoders.Length == 1 ? "" : "s")}");
                    await factory.Create(decoder).ConfigureDecoderFeedsAsync(encoders, CancellationToken.None);
                    CompleteStep(decoder, "Decoder feed presets", encoders.Length == 0
                        ? "Preset bank cleared; no encoder feeds available"
                        : $"All {encoders.Length} advertising encoder feed{(encoders.Length == 1 ? "" : "s")} stored" +
                          (stoppedTeleTools == 0 ? "" : $"; {stoppedTeleTools} stopped TeleTool omitted until it has an active NDI source"));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not populate decoder feed presets on {Device}", decoder.Id);
                    await SaveDeviceAsync(decoder with { Health = DeviceHealth.Error, LastError = ex.Message });
                    FailStep(decoder, "Decoder feed presets", ex.Message);
                }
            });

            await store.UpdateAsync(s => s with
            {
                LastJob = new(plan.Settings.JobName, plan.Settings.StaticStart, plan.Settings.StaticEnd, plan.Settings.NdiDiscoveryServerIp, Progress.StartedUtc)
                {
                    KiloLinkServerIp = plan.Settings.KiloLinkServerIp,
                    KiloLinkWebPort = plan.Settings.KiloLinkWebPort,
                    Simulation = plan.Devices.Count > 0 && plan.Devices.All(d => d.Family is DeviceFamily.Simulated or DeviceFamily.SimulatedTeleTool)
                },
                FirmwareJob = CompleteFirmwareJob(s.FirmwareJob)
            });
            var readyIds = ready.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var needsRoleSelection = (await store.ReadAsync()).Devices.Any(device => readyIds.Contains(device.Id) && device.Role == DeviceRole.Unknown);
            Finish(Progress.Steps.Any(s => s.Status == "error")
                ? "completed-with-errors"
                : needsRoleSelection ? "awaiting-role-selection" : ready.Count > 0 ? "awaiting-decoder-names" : "completed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Onboarding run failed");
            Finish("failed");
        }
    }

    private async Task ValidateFirmwareCoverageAsync(OnboardingPlan plan)
    {
        var required = plan.Devices
            .Where(device => device.Family is DeviceFamily.N6 or DeviceFamily.N60)
            .Select(device => device.Family == DeviceFamily.N60 ? "N60" : "N6")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (required.Length == 0) return;
        var job = (await store.ReadAsync()).FirmwareJob
            ?? throw new InvalidOperationException("Stage the latest model firmware before starting Kiloview onboarding.");
        foreach (var model in required)
        {
            var package = job.Packages.FirstOrDefault(candidate => string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase));
            if (package is null || !File.Exists(package.LocalPath))
                throw new InvalidOperationException($"Stage the latest {model} firmware before starting onboarding.");
        }
    }

    private async Task PrepareCleanOnboardingAsync(OnboardingPlan plan, CancellationToken ct)
    {
        var selectedDevices = plan.Devices.ToDictionary(
            device => device.DeviceId,
            device => device.CurrentIp,
            StringComparer.Ordinal);
        if (plan.Devices.Any(device => device.Family is DeviceFamily.N6 or DeviceFamily.N60))
        {
            var credential = credentialStore.ResolveAndStore(
                plan.Settings.KiloLinkServerIp,
                plan.Settings.KiloLinkUsername,
                plan.Settings.KiloLinkPassword);
            await kiloLink.ClearDeviceInventoryAsync(
                plan.Settings.KiloLinkServerIp,
                plan.Settings.KiloLinkWebPort,
                credential,
                ct);
        }
        titleCards.StopAll();
        await store.UpdateAsync(state => state with
        {
            Devices = state.Devices
                .Where(device => selectedDevices.TryGetValue(device.Id, out var address)
                    && string.Equals(device.IpAddress, address, StringComparison.Ordinal))
                .DistinctBy(device => device.Id, StringComparer.Ordinal)
                .ToArray(),
            LastJob = null,
            Multicast = null,
            TeleToolManagerId = null,
            RemoteWindowsPcs = null
        });
    }

    private async Task<ManagedDevice> ApplyFirmwareBeforeConfigurationAsync(ManagedDevice device)
    {
        if (device.IsSimulation()) return device;
        var job = (await store.ReadAsync()).FirmwareJob
            ?? throw new InvalidOperationException("The staged firmware job is no longer available.");
        var model = device.Family == DeviceFamily.N60 ? "N60" : "N6";
        var package = job.Packages.First(candidate => string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase));
        if (PackageMatchesInstalledVersion(package, device)) return device;

        await factory.Create(device).UpdateFirmwareAsync(package, CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(8);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var read = await factory.Create(device).ReadAsync(CancellationToken.None);
                read = read with
                {
                    Id = device.Id,
                    Credentials = device.Credentials,
                    LicenseAccepted = device.LicenseAccepted,
                    Health = DeviceHealth.Configuring
                };
                if (PackageMatchesInstalledVersion(package, read))
                {
                    await SaveDeviceAsync(read);
                    return read;
                }
                last = new InvalidOperationException($"Device returned on firmware {read.FirmwareVersion}, waiting for the staged version.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DeviceApiException or InvalidOperationException or System.Text.Json.JsonException)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        throw new DeviceApiException($"{model} did not return on the staged firmware within eight minutes.", last);
    }

    private FirmwareJob? CompleteFirmwareJob(FirmwareJob? job)
    {
        if (job is null) return null;
        var failed = Progress.Steps.Any(step => step.Step == "Firmware" && step.Status == "error");
        return job with
        {
            Status = failed ? "failed" : "completed",
            FinishedUtc = DateTimeOffset.UtcNow,
            Message = failed
                ? "One or more devices failed their pre-configuration firmware update."
                : "Selected Kiloview firmware versions were verified before network and KiloLink configuration."
        };
    }

    private static bool PackageMatchesInstalledVersion(FirmwarePackage package, ManagedDevice device) =>
        !string.IsNullOrWhiteSpace(device.FirmwareVersion) &&
        package.FileName.Contains(device.FirmwareVersion, StringComparison.OrdinalIgnoreCase);

    private async Task OnboardTeleToolAsync(ManagedDevice device, DevicePlan item, OnboardingRequest settings)
    {
        if (!device.CanOnboard)
            throw new InvalidOperationException(device.ManagementMessage ?? "This TeleTool cannot be onboarded.");

        var channelName = $"{SanitizeName(settings.JobName)}-{item.TargetIp.Split('.').Last()}";
        Step(device, "TeleTool Dev API", "running", $"Checking for {TeleToolFleetService.RequiredDevVersion} or later");
        CompleteStep(device, "TeleTool Dev API", $"NDI group and Discovery Server API available on {device.FirmwareVersion ?? "Dev build"}");

        Step(device, "Identity & NDI", "running", $"Applying {item.Hostname}, channel {channelName}, and group {settings.JobName}");
        await factory.Create(device).ConfigureOnboardingAsync(settings, item.Hostname, channelName, CancellationToken.None);
        device = device with
        {
            Hostname = item.Hostname,
            NdiChannelName = channelName,
            NdiGroup = settings.JobName,
            Role = DeviceRole.Encoder,
            Health = DeviceHealth.Configuring,
            LastError = null
        };
        await SaveDeviceAsync(device);
        CompleteStep(device, "Identity & NDI", $"Discovery Server {settings.NdiDiscoveryServerIp}; group '{settings.JobName}'");

        Step(device, "Static IP & DNS", "running", item.ExistingStaticDevice
            ? $"Retaining existing network settings at {item.TargetIp}"
            : $"Assigning {item.TargetIp} on TeleTool eth0; DNS {settings.Dns}");
        if (!item.ExistingStaticDevice)
        {
            await factory.Create(device).SetNetworkAsync(
                item.TargetIp,
                settings.SubnetMask,
                settings.Gateway,
                settings.Dns,
                CancellationToken.None);
        }
        device = device with { IpAddress = item.TargetIp, IsStatic = true, Health = DeviceHealth.Configuring };
        await SaveDeviceAsync(device);
        CompleteStep(device, "Static IP & DNS", item.ExistingStaticDevice
            ? "Already in static range; network settings unchanged"
            : $"Address assigned; DNS {settings.Dns} applied");

        Step(device, "Reconnect", "running", $"Waiting for TeleTool at {item.TargetIp}:{device.WebPort}");
        device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(60));
        CompleteStep(device, "Reconnect", "TeleTool web API reachable on its static address");

        Step(device, "Fleet adoption", "running", "Registering this configurator as the active Fleet Manager");
        device = await WaitForDeviceAsync(device with
        {
            IsOnboarded = true,
            Role = DeviceRole.Encoder,
            Health = DeviceHealth.Configuring,
            ManagementState = "managed",
            ManagementMessage = "Managed by this configurator"
        }, TimeSpan.FromSeconds(30));
        device = device with
        {
            IsOnboarded = true,
            Role = DeviceRole.Encoder,
            Health = DeviceHealth.Online,
            ManagementState = "managed",
            ManagementMessage = "Managed by this configurator",
            LastError = null
        };
        await SaveDeviceAsync(device);
        CompleteStep(device, "Fleet adoption", "Monitoring heartbeat and stream controls enabled");
    }

    public async Task<ManagedDevice> SetRoleAsync(string id, DeviceRole role, CancellationToken ct)
    {
        if (role == DeviceRole.Unknown) throw new ArgumentException("Choose Encoder or Decoder.");
        var device = await GetDeviceAsync(id);
        await factory.Create(device).SetRoleAsync(role, ct);
        device = device with { Role = role, Health = DeviceHealth.Configuring, LastError = null };
        await SaveDeviceAsync(device);
        device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 90 : 60), ct);
        device = device with
        {
            Role = role,
            Health = DeviceHealth.Online,
            LastError = null,
            ManagementState = null,
            ManagementMessage = null,
            HdmiDisplayConnected = null,
            HdmiOutputResolution = null
        };
        await SaveDeviceAsync(device);
        return device;
    }

    public async Task<HdmiInputProbeResult> ProbeEncoderInputAsync(string id, CancellationToken ct)
    {
        var device = await GetDeviceAsync(id);
        if (!device.IsKiloview())
            throw new InvalidOperationException("HDMI role detection is available only for Kiloview devices.");
        return await factory.Create(device).ProbeEncoderInputAsync(ct);
    }

    public async Task<KiloviewRemovalResult> RemoveKiloviewAsync(string id, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var device = state.Devices.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException($"Device '{id}' was not found.");
        if (!device.IsKiloview()) throw new InvalidOperationException("Only Kiloview devices can be removed with this action.");
        if (!device.IsOnboarded) throw new InvalidOperationException("This Kiloview is not onboarded.");

        var kiloLinkRecordRemoved = false;
        if (!device.IsSimulation())
        {
            var job = state.LastJob ?? throw new InvalidOperationException("The KiloLink server for this job is no longer available.");
            if (string.IsNullOrWhiteSpace(job.KiloLinkServerIp))
                throw new InvalidOperationException("The KiloLink server for this job was not retained.");
            var credential = credentialStore.ResolveAndStore(job.KiloLinkServerIp, null, null);
            kiloLinkRecordRemoved = await kiloLink.RemoveDeviceAsync(
                job.KiloLinkServerIp,
                job.KiloLinkWebPort,
                credential,
                device.Id,
                ct);
        }

        titleCards.Forget(device.Id);
        thumbnails.Forget(device.Id);
        var updated = await store.UpdateAsync(current =>
        {
            var assignments = current.Multicast?.Assignments
                .Where(assignment => !string.Equals(assignment.EndpointId, device.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return current with
            {
                Devices = current.Devices.Where(candidate => candidate.Id != device.Id).ToArray(),
                Multicast = current.Multicast is null
                    ? null
                    : assignments is null || assignments.Length == 0
                        ? null
                        : current.Multicast with { Assignments = assignments }
            };
        });
        var remaining = updated.Devices.Count(candidate => candidate.IsOnboarded && candidate.IsKiloview());
        logger.LogInformation("Removed Kiloview {DeviceId} at {Address} from the job; KiloLink record removed: {KiloLinkRecordRemoved}",
            device.Id, device.IpAddress, kiloLinkRecordRemoved);
        return new(device.Id, device.Hostname, remaining, kiloLinkRecordRemoved, "removed");
    }

    public async Task<ManagedDevice> SetIdentityAsync(string id, IdentityUpdate update, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(update.Hostname) || string.IsNullOrWhiteSpace(update.NdiChannelName))
            throw new ArgumentException("Hostname and NDI channel name are required.");
        var device = await GetDeviceAsync(id);
        var requestedHostname = update.Hostname.Trim();
        var requestedChannel = update.NdiChannelName.Trim();
        var decoder = device.Role == DeviceRole.Decoder;
        if (device.IsKiloview() && !device.IsSimulation())
        {
            if (!string.Equals(device.Hostname, requestedHostname, StringComparison.Ordinal))
            {
                await factory.Create(device).SetHostnameAsync(requestedHostname, ct);
                device = device with { Hostname = requestedHostname, Health = DeviceHealth.Configuring, LastError = null };
                await SaveDeviceAsync(device);
                // N60 hostname changes can restart its web/codec services after
                // the API call has already returned. Let that restart begin,
                // then require a stable authenticated read before continuing.
                await Task.Delay(TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 5 : 2), ct);
                var refreshed = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 120 : 60), ct);
                device = refreshed with
                {
                    Hostname = requestedHostname,
                    NdiChannelName = requestedChannel,
                    NdiGroup = device.NdiGroup,
                    Role = device.Role,
                    IsOnboarded = device.IsOnboarded,
                    Health = DeviceHealth.Online,
                    LastError = null
                };
            }
            // Decoder channel names are used by the identity card and stored
            // job metadata. Do not bounce the unit through encoder mode merely
            // to write inactive sender settings.
            if (!decoder)
                await factory.Create(device).SetIdentityAsync(requestedHostname, requestedChannel, device.NdiGroup, ct);
        }
        else
        {
            await factory.Create(device).SetIdentityAsync(requestedHostname, requestedChannel, device.NdiGroup, ct);
        }
        device = device with { Hostname = requestedHostname, NdiChannelName = requestedChannel, LastError = null };
        var state = await store.ReadAsync();
        if (device.IsKiloview() && !device.IsSimulation() && state.LastJob is { } job && !string.IsNullOrWhiteSpace(job.KiloLinkServerIp))
        {
            var credential = credentialStore.ResolveAndStore(job.KiloLinkServerIp, null, null);
            await kiloLink.AuthorizeDeviceAsync(job.KiloLinkServerIp, job.KiloLinkWebPort, credential, device.Id, device.Hostname, ct);
        }
        if (decoder)
        {
            device = device with
            {
                Hostname = requestedHostname,
                NdiChannelName = requestedChannel,
                Role = DeviceRole.Decoder,
                Health = DeviceHealth.Online,
                LastError = null
            };
            await SaveDeviceAsync(device);
            var source = await titleCards.StartOrUpdateAsync(device, ct);
            await factory.Create(device).ShowIdentityAsync(source, ct);
        }
        else if (device.Family == DeviceFamily.Simulated && device.Role == DeviceRole.Decoder)
        {
            await titleCards.StartOrUpdateAsync(device, ct);
        }
        await SaveDeviceAsync(device);
        return device;
    }

    public async Task<object> PrepareDecoderIdentificationAsync(CancellationToken ct)
    {
        var decoders = (await store.ReadAsync()).Devices.Where(d => d.IsOnboarded && d.Role == DeviceRole.Decoder).ToArray();
        var active = new ConcurrentBag<string>();
        var cards = new ConcurrentBag<object>();
        var errors = new ConcurrentBag<object>();
        await Parallel.ForEachAsync(decoders, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (device, token) =>
        {
            try
            {
                var source = await titleCards.StartOrUpdateAsync(device, token);
                await factory.Create(device).ShowIdentityAsync(source, token);
                active.Add(device.Id);
                cards.Add(new
                {
                    device.Id,
                    source.Name,
                    device.Hostname,
                    device.IpAddress,
                    device.NdiGroup,
                    device.NdiChannelName
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not show identity card on {Device}", device.Id);
                errors.Add(new { device.Id, device.IpAddress, ex.Message });
            }
        });
        return new
        {
            active = active.OrderBy(id => id).ToArray(),
            cards = cards.ToArray(),
            errors = errors.ToArray()
        };
    }

    public async Task<object> CompleteAsync(CancellationToken ct)
    {
        var decoders = (await store.ReadAsync()).Devices.Where(d => d.IsOnboarded && d.Role == DeviceRole.Decoder).ToArray();
        var errors = new ConcurrentBag<object>();
        titleCards.StopAll();
        await Parallel.ForEachAsync(decoders, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (device, token) =>
        {
            try { await factory.Create(device).BlankAsync(token); }
            catch (Exception ex) { errors.Add(new { device.Id, ex.Message }); }
        });
        return new { completed = errors.IsEmpty, decoders = decoders.Length, errors = errors.ToArray() };
    }

    private async Task<ManagedDevice> GetDeviceAsync(string id) => (await store.ReadAsync()).Devices.FirstOrDefault(d => d.Id == id)
        ?? throw new KeyNotFoundException($"Device '{id}' was not found.");

    private async Task SaveDeviceAsync(ManagedDevice device) => await store.UpdateAsync(s =>
        s with { Devices = s.Devices.Select(d => d.Id == device.Id ? device : d).ToArray() });

    private async Task ReplaceDeviceAsync(string discoveredDeviceId, ManagedDevice device) => await store.UpdateAsync(s =>
        s with
        {
            Devices = s.Devices
                .Where(d => d.Id != discoveredDeviceId && d.Id != device.Id)
                .Append(device)
                .OrderBy(d => d.IpAddress)
                .ToArray()
        });

    private async Task<ManagedDevice> WaitForDeviceAsync(ManagedDevice device, TimeSpan timeout, CancellationToken ct = default)
    {
        if (device.IsSimulation()) return await factory.Create(device).ReadAsync(ct);
        var end = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var read = await factory.Create(device).ReadAsync(ct);
                return read with
                {
                    Id = device.Id,
                    IsOnboarded = device.IsOnboarded,
                    NdiGroup = device.NdiGroup,
                    NdiChannelName = device.NdiChannelName
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DeviceApiException or InvalidOperationException or System.Text.Json.JsonException) { last = ex; }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new DeviceApiException($"Device did not become reachable at {device.IpAddress} within {timeout.TotalSeconds:0} seconds.", last);
    }

    private static async Task<bool> AddressRespondsAsync(string address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            if ((await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(300), cancellationToken: ct)).Status == IPStatus.Success) return true;
        }
        catch (Exception ex) when (ex is PingException or OperationCanceledException) { if (ct.IsCancellationRequested) throw; }
        foreach (var port in new[] { 80, 443, TeleToolFleetService.DefaultPort })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(300);
            try { using var tcp = new TcpClient(); await tcp.ConnectAsync(address, port, timeout.Token); return true; }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { if (ct.IsCancellationRequested) throw; }
        }
        return false;
    }

    private void Step(ManagedDevice device, string name, string status, string? message = null)
    {
        lock (_progressGate) _progress = _progress with { Steps = _progress.Steps.Append(new(device.Id, device.IpAddress, name, status, message)).ToArray() };
    }

    private void CompleteStep(ManagedDevice device, string name, string? message = null)
    {
        lock (_progressGate)
        {
            var steps = _progress.Steps.ToList();
            var index = steps.FindLastIndex(s => s.DeviceId == device.Id && s.Step == name && s.Status == "running");
            if (index >= 0) steps[index] = steps[index] with { Status = "ok", Message = message, IpAddress = device.IpAddress };
            else steps.Add(new(device.Id, device.IpAddress, name, "ok", message));
            _progress = _progress with { Steps = steps, Completed = Math.Min(_progress.Total, _progress.Completed + 1) };
        }
    }

    private void FailStep(ManagedDevice device, string name, string message)
    {
        lock (_progressGate)
        {
            var steps = _progress.Steps.ToList();
            var index = steps.FindLastIndex(step => step.DeviceId == device.Id && step.Step == name && step.Status == "running");
            if (index < 0) index = steps.FindLastIndex(step => step.DeviceId == device.Id && step.Status == "running");
            if (index >= 0)
                steps[index] = steps[index] with { Status = "error", Message = message, IpAddress = device.IpAddress };
            else
                steps.Add(new(device.Id, device.IpAddress, name, "error", message));
            _progress = _progress with { Steps = steps, Completed = Math.Min(_progress.Total, _progress.Completed + 1) };
        }
    }

    private void AddProgressWork(int count)
    {
        if (count <= 0) return;
        lock (_progressGate) _progress = _progress with { Total = _progress.Total + count };
    }

    private void Finish(string status)
    {
        lock (_progressGate)
        {
            var steps = _progress.Steps.Select(step => step.Status == "running"
                ? step with { Status = "error", Message = step.Message ?? "The run finished before this step completed." }
                : step).ToArray();
            _progress = _progress with
            {
                Status = status,
                FinishedUtc = DateTimeOffset.UtcNow,
                Completed = _progress.Total,
                Steps = steps
            };
        }
    }

    private static string SanitizeName(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-')[..Math.Min(new string(chars).Trim('-').Length, 32)];
    }
}
