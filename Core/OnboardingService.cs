using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using KiloviewSetup.Devices;

namespace KiloviewSetup.Core;

public sealed class OnboardingService(AppStateStore store, DeviceClientFactory factory, KiloLinkCredentialStore credentialStore, KiloLinkServerClient kiloLink, NdiTitleCardService titleCards, ILogger<OnboardingService> logger)
{
    private readonly object _progressGate = new();
    private OnboardingProgress _progress = new(Guid.Empty, "idle", 0, 0, [], DateTimeOffset.UtcNow);
    private Task? _run;
    public OnboardingProgress Progress { get { lock (_progressGate) return _progress; } }

    public async Task<OnboardingPlan> BuildPlanAsync(OnboardingRequest request, CancellationToken ct)
    {
        InputValidation.Validate(request);
        var state = await store.ReadAsync();
        var selected = request.DeviceIds.Distinct().Select(id => state.Devices.FirstOrDefault(d => d.Id == id)
            ?? throw new ArgumentException($"Selected device '{id}' is no longer in the discovery list.")).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Select at least one device to onboard.");
        if (selected.Any(d => d.Family != DeviceFamily.Simulated))
        {
            var serverCredential = credentialStore.ResolveAndStore(request.KiloLinkServerIp, request.KiloLinkUsername, request.KiloLinkPassword);
            request = request with { KiloLinkUsername = serverCredential.Username, KiloLinkPassword = "" };
        }
        else request = request with { KiloLinkPassword = "" };

        var range = NetworkAddressing.Range(request.StaticStart, request.StaticEnd).Select(x => x.ToString()).ToArray();
        var occupied = new ConcurrentDictionary<string, byte>();
        foreach (var device in state.Devices.Where(d => d.IsStatic || d.IsOnboarded))
            if (range.Contains(device.IpAddress)) occupied.TryAdd(device.IpAddress, 0);

        await Parallel.ForEachAsync(range, new ParallelOptions { MaxDegreeOfParallelism = 128, CancellationToken = ct }, async (address, token) =>
        {
            if (occupied.ContainsKey(address)) return;
            if (await AddressRespondsAsync(address, token)) occupied.TryAdd(address, 0);
        });

        var occupiedNumbers = occupied.Keys.Select(x => NetworkAddressing.ToUInt(IPAddress.Parse(x))).ToArray();
        var startNumber = NetworkAddressing.ToUInt(IPAddress.Parse(request.StaticStart));
        var next = occupiedNumbers.Length == 0 ? startNumber : Math.Max(startNumber, occupiedNumbers.Max() + 1);
        var end = NetworkAddressing.ToUInt(IPAddress.Parse(request.StaticEnd));
        var plans = new List<DevicePlan>();
        for (var i = 0; i < selected.Length; i++)
        {
            var device = selected[i];
            if (device.IsStatic && range.Contains(device.IpAddress))
            {
                plans.Add(new(device.Id, device.IpAddress, device.IpAddress, $"{SanitizeName(request.JobName)}-KV-{i + 1:000}", device.Role, true));
                continue;
            }
            while (next <= end && occupied.ContainsKey(NetworkAddressing.FromUInt(next).ToString())) next++;
            if (next > end) throw new ArgumentException("There are not enough unused addresses above the previously onboarded devices in the static range.");
            var target = NetworkAddressing.FromUInt(next++).ToString();
            var role = request.RoleOverrides is not null && request.RoleOverrides.TryGetValue(device.Id, out var value) ? value : DeviceRole.Unknown;
            plans.Add(new(device.Id, device.IpAddress, target, $"{SanitizeName(request.JobName)}-KV-{i + 1:000}", role));
            occupied.TryAdd(target, 0);
        }

        var warnings = new List<string>
        {
            $"Confirming authorizes the application to accept the Kiloview EULA on each selected unit and set its device login to admin / {request.JobName}.",
            "KiloLink authorization codes will be generated on the server from each unit serial number; the KiloLink alias will match the assigned hostname.",
            "Role detection temporarily switches every selected unit to decoder mode. N60 firmware can take about one minute to change mode.",
            "Keep all intended HDMI displays powered on until role detection completes."
        };
        return new(request, plans, occupied.Keys.OrderBy(x => NetworkAddressing.ToUInt(IPAddress.Parse(x))).ToArray(), warnings);
    }

    public object Start(OnboardingPlan plan)
    {
        lock (_progressGate)
        {
            if (_run is { IsCompleted: false }) throw new InvalidOperationException("An onboarding run is already in progress.");
            titleCards.StopAll();
            var total = Math.Max(1, plan.Devices.Count * 8);
            _progress = new(Guid.NewGuid(), "running", 0, total, [], DateTimeOffset.UtcNow);
            _run = Task.Run(() => ExecuteAsync(plan));
            return new { _progress.RunId, _progress.Status };
        }
    }

    private async Task ExecuteAsync(OnboardingPlan plan)
    {
        var ready = new List<ManagedDevice>();
        KiloLinkCredential? serverCredential = null;
        try
        {
            foreach (var item in plan.Devices)
            {
                var device = (await store.ReadAsync()).Devices.First(d => d.Id == item.DeviceId);
                try
                {
                    var targetCredentials = new DeviceCredentials("admin", plan.Settings.JobName);
                    Step(device, "Access & license", "running", "Accepting EULA and applying job credentials");
                    await factory.Create(device).ProvisionAccessAsync(targetCredentials, CancellationToken.None);
                    device = device with { Credentials = targetCredentials, LicenseAccepted = true, Health = DeviceHealth.Configuring };
                    await SaveDeviceAsync(device);
                    CompleteStep(device, "Access & license", "EULA accepted; admin password set to Job Name");

                    Step(device, "Static IP", "running", $"Assigning {item.TargetIp}");
                    if (!item.ExistingStaticDevice)
                        await factory.Create(device).SetNetworkAsync(item.TargetIp, plan.Settings.SubnetMask, plan.Settings.Gateway, CancellationToken.None);
                    device = device with { IpAddress = item.TargetIp, IsStatic = true, Health = DeviceHealth.Configuring };
                    await SaveDeviceAsync(device);
                    CompleteStep(device, "Static IP", item.ExistingStaticDevice ? "Already in static range" : "Address assigned");

                    Step(device, "Reconnect", "running");
                    device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 90 : 45));
                    CompleteStep(device, "Reconnect", "Device reachable on static IP");

                    if (device.Role == DeviceRole.Decoder)
                    {
                        Step(device, "Prepare", "running", "Temporarily switching to encoder mode for NDI setup");
                        await factory.Create(device).SetRoleAsync(DeviceRole.Encoder, CancellationToken.None);
                        device = device with { Role = DeviceRole.Encoder };
                        device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 90 : 45));
                        CompleteStep(device, "Prepare", "Encoder mode ready");
                    }
                    else CompleteStep(device, "Prepare", "Encoder mode ready");

                    Step(device, "KiloLink authorization", "running", "Generating server-side device code");
                    var authorizationCode = device.Family == DeviceFamily.Simulated
                        ? $"SIM-{device.Id}"
                        : (await kiloLink.AuthorizeDeviceAsync(
                            plan.Settings.KiloLinkServerIp,
                            plan.Settings.KiloLinkWebPort,
                            serverCredential ??= credentialStore.ResolveAndStore(plan.Settings.KiloLinkServerIp, plan.Settings.KiloLinkUsername, plan.Settings.KiloLinkPassword),
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
                    CompleteStep(device, "KiloLink / NDI", $"NDI group '{plan.Settings.JobName}' applied from Job Name");
                    ready.Add(device);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to onboard {Device}", item.DeviceId);
                    await SaveDeviceAsync(device with { Health = DeviceHealth.Error, LastError = ex.Message });
                    FailStep(device, "Onboarding", ex.Message);
                }
            }

            // Start every mode change first, then allow a single negotiation window for all displays.
            foreach (var device in ready)
            {
                try
                {
                    Step(device, "HDMI probe", "running", "Switching to decoder mode");
                    if (device.Role != DeviceRole.Decoder) await factory.Create(device).SetRoleAsync(DeviceRole.Decoder, CancellationToken.None);
                    await SaveDeviceAsync(device with { Role = DeviceRole.Decoder, Health = DeviceHealth.Configuring });
                }
                catch (Exception ex) { FailStep(device, "HDMI probe", ex.Message); }
            }

            if (ready.Any(d => d.Family != DeviceFamily.Simulated)) await Task.Delay(TimeSpan.FromSeconds(70));
            else await Task.Delay(350);

            foreach (var original in ready)
            {
                var device = (await store.ReadAsync()).Devices.First(d => d.Id == original.Id);
                try
                {
                    device = await WaitForDeviceAsync(device, TimeSpan.FromSeconds(30));
                    var probe = await factory.Create(device).ProbeHdmiAsync(CancellationToken.None);
                    var overrideRole = DeviceRole.Unknown;
                    var forced = plan.Settings.RoleOverrides is not null && plan.Settings.RoleOverrides.TryGetValue(device.Id, out overrideRole) && overrideRole != DeviceRole.Unknown;
                    var role = forced ? overrideRole : probe.Connected ? DeviceRole.Decoder : DeviceRole.Encoder;
                    if (role == DeviceRole.Encoder)
                    {
                        await factory.Create(device).SetRoleAsync(DeviceRole.Encoder, CancellationToken.None);
                    }
                    device = device with
                    {
                        Role = role,
                        HdmiDisplayConnected = probe.Connected,
                        HdmiOutputResolution = probe.NegotiatedResolution,
                        Health = DeviceHealth.Online,
                        LastError = null
                    };
                    await SaveDeviceAsync(device);
                    CompleteStep(device, "HDMI probe", forced ? $"Role overridden to {role}" : probe.Connected ? $"Decoder — negotiated {probe.NegotiatedResolution}" : "No negotiated output; returned to encoder mode");

                    // Identity is persisted now; decoder cards can fine-tune both names on the next UI page.
                    CompleteStep(device, "Identity", device.Hostname);
                }
                catch (Exception ex)
                {
                    await SaveDeviceAsync(device with { Health = DeviceHealth.Error, LastError = ex.Message });
                    FailStep(device, "HDMI probe", ex.Message);
                }
            }

            await store.UpdateAsync(s => s with
            {
                LastJob = new(plan.Settings.JobName, plan.Settings.StaticStart, plan.Settings.StaticEnd, plan.Settings.NdiDiscoveryServerIp, Progress.StartedUtc)
                {
                    KiloLinkServerIp = plan.Settings.KiloLinkServerIp,
                    KiloLinkWebPort = plan.Settings.KiloLinkWebPort,
                    Simulation = ready.Count > 0 && ready.All(d => d.Family == DeviceFamily.Simulated)
                },
                FirmwareJob = null
            });
            Finish(Progress.Steps.Any(s => s.Status == "error") ? "completed-with-errors" : "awaiting-decoder-names");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Onboarding run failed");
            Finish("failed");
        }
    }

    public async Task<ManagedDevice> SetRoleAsync(string id, DeviceRole role, CancellationToken ct)
    {
        var device = await GetDeviceAsync(id);
        await factory.Create(device).SetRoleAsync(role, ct);
        device = device with { Role = role, Health = DeviceHealth.Configuring, LastError = null };
        await SaveDeviceAsync(device);
        return device;
    }

    public async Task<ManagedDevice> SetIdentityAsync(string id, IdentityUpdate update, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(update.Hostname) || string.IsNullOrWhiteSpace(update.NdiChannelName))
            throw new ArgumentException("Hostname and NDI channel name are required.");
        var device = await GetDeviceAsync(id);
        var restoreDecoder = device.Role == DeviceRole.Decoder && device.Family != DeviceFamily.Simulated;
        if (restoreDecoder)
        {
            await factory.Create(device).SetRoleAsync(DeviceRole.Encoder, ct);
            device = await WaitForDeviceAsync(device with { Role = DeviceRole.Encoder }, TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 90 : 45), ct);
        }
        await factory.Create(device).SetIdentityAsync(update.Hostname.Trim(), update.NdiChannelName.Trim(), device.NdiGroup, ct);
        device = device with { Hostname = update.Hostname.Trim(), NdiChannelName = update.NdiChannelName.Trim(), LastError = null };
        var state = await store.ReadAsync();
        if (device.Family != DeviceFamily.Simulated && state.LastJob is { } job && !string.IsNullOrWhiteSpace(job.KiloLinkServerIp))
        {
            var credential = credentialStore.ResolveAndStore(job.KiloLinkServerIp, null, null);
            await kiloLink.AuthorizeDeviceAsync(job.KiloLinkServerIp, job.KiloLinkWebPort, credential, device.Id, device.Hostname, ct);
        }
        if (restoreDecoder)
        {
            await factory.Create(device).SetRoleAsync(DeviceRole.Decoder, ct);
            device = await WaitForDeviceAsync(device with { Role = DeviceRole.Decoder, Health = DeviceHealth.Configuring }, TimeSpan.FromSeconds(device.Family == DeviceFamily.N60 ? 90 : 45), ct);
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

    private async Task<ManagedDevice> WaitForDeviceAsync(ManagedDevice device, TimeSpan timeout, CancellationToken ct = default)
    {
        if (device.Family == DeviceFamily.Simulated) return await factory.Create(device).ReadAsync(ct);
        var end = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var read = await factory.Create(device).ReadAsync(ct);
                return read with { IsOnboarded = device.IsOnboarded, NdiGroup = device.NdiGroup, NdiChannelName = device.NdiChannelName };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DeviceApiException) { last = ex; }
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
        foreach (var port in new[] { 80, 443 })
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
            var steps = _progress.Steps.Append(new OnboardingStep(device.Id, device.IpAddress, name, "error", message)).ToArray();
            _progress = _progress with { Steps = steps, Completed = Math.Min(_progress.Total, _progress.Completed + 1) };
        }
    }

    private void Finish(string status)
    {
        lock (_progressGate) _progress = _progress with { Status = status, FinishedUtc = DateTimeOffset.UtcNow, Completed = _progress.Total };
    }

    private static string SanitizeName(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-')[..Math.Min(new string(chars).Trim('-').Length, 32)];
    }
}
