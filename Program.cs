using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NDIJobConfigurator.Core;
using NDIJobConfigurator.Devices;
using Microsoft.AspNetCore.Mvc;

var servicePort = int.TryParse(
        Environment.GetEnvironmentVariable("NDI_JOB_CONFIGURATOR_SERVICE_PORT")
            ?? Environment.GetEnvironmentVariable("KILOVIEW_SERVICE_PORT"),
        out var configuredPort)
    && configuredPort is >= 1024 and <= 65535 ? configuredPort : 8091;
var lanAccess = args.Contains("--lan", StringComparer.OrdinalIgnoreCase)
    || string.Equals(
        Environment.GetEnvironmentVariable("NDI_JOB_CONFIGURATOR_LAN_ACCESS")
            ?? Environment.GetEnvironmentVariable("KILOVIEW_LAN_ACCESS"),
        "1",
        StringComparison.Ordinal);
using var instanceSemaphore = new Semaphore(1, 1, $"Local\\NDIJobConfigurator-{servicePort}");
if (lanAccess && servicePort != 8091)
    throw new InvalidOperationException("Production LAN access requires TCP 8091. Nondefault service ports are supported only for isolated loopback testing.");
var ownsInstanceSemaphore = instanceSemaphore.WaitOne(0);
if (!ownsInstanceSemaphore)
{
    try { Process.Start(new ProcessStartInfo($"http://localhost:{servicePort}") { UseShellExecute = true }); }
    catch { /* The existing tray process remains available even if no browser is registered. */ }
    return;
}

var builder = WebApplication.CreateBuilder(args);
var logDirectory = AppDataPaths.ResolveLogDirectory(builder.Environment.ContentRootPath);
builder.Logging.ClearProviders();
builder.Logging.AddDebug();
builder.Logging.AddProvider(new RollingFileLoggerProvider(logDirectory));
builder.WebHost.UseUrls($"http://{(lanAccess ? "0.0.0.0" : "127.0.0.1")}:{servicePort}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    var resolver = new DefaultJsonTypeInfoResolver();
    resolver.Modifiers.Add(typeInfo =>
    {
        if (typeInfo.Type != typeof(ManagedDevice)) return;
        var credentials = typeInfo.Properties.FirstOrDefault(property =>
            property.Name.Equals(nameof(ManagedDevice.Credentials), StringComparison.OrdinalIgnoreCase));
        if (credentials is not null) credentials.ShouldSerialize = static (_, _) => false;
    });
    o.SerializerOptions.TypeInfoResolver = resolver;
});
builder.Services.AddSingleton<AppStateStore>();
builder.Services.AddSingleton<OnboardingDiagnosticsStore>();
builder.Services.AddHostedService<OnboardingDiagnosticsCleanup>();
builder.Services.AddSingleton<KiloLinkCredentialStore>();
builder.Services.AddSingleton<KiloLinkServerClient>();
builder.Services.AddSingleton<KiloLinkConnectionService>();
builder.Services.AddSingleton<NdiDiscoveryServerClient>();
builder.Services.AddSingleton<NdiTitleCardService>();
builder.Services.AddSingleton<EncoderThumbnailService>();
builder.Services.AddSingleton<FirmwareService>();
builder.Services.AddSingleton<TeleToolFleetIdentity>();
builder.Services.AddSingleton<TeleToolFleetService>();
builder.Services.AddSingleton<DeviceClientFactory>();
builder.Services.AddSingleton<NetworkDiscovery>();
builder.Services.AddSingleton<OnboardingService>();
builder.Services.AddSingleton<ILocalPcOnboarding, LocalPcOnboardingService>();
builder.Services.AddSingleton<MulticastService>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddSingleton<WindowsPcAgentService>();
builder.Services.AddSingleton<WindowsPcRemoteOnboardingService>();
builder.Services.AddSingleton<DeviceUiGatewayService>();
builder.Services.AddSingleton<SystemTrayService>();
builder.Services.AddHostedService(services => services.GetRequiredService<SystemTrayService>());
builder.Services.AddHostedService(services => services.GetRequiredService<WindowsPcAgentService>());
builder.Services.AddHttpClient<GitHubUpdateService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NDI-Job-Configurator");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddHttpClient("TeleTool", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("KiloviewDevice")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxConnectionsPerServer = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        UseCookies = false
    });
builder.Services.AddHttpClient("KiloLinkDiscovery")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromMilliseconds(850),
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        UseCookies = false
    });
builder.Services.AddHttpClient("KiloLinkServer")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        UseCookies = false
    });
builder.Services.AddHttpClient("WindowsPcAgent", client => client.Timeout = TimeSpan.FromSeconds(3))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        UseProxy = false,
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    });
builder.Services.AddHostedService<DeviceMonitor>();

var app = builder.Build();
WindowsInstallationRegistration.Ensure(app.Environment.ContentRootPath, app.Logger);
app.Use(async (context, next) =>
{
    var state = await context.RequestServices.GetRequiredService<AppStateStore>().ReadAsync();
    var network = NetworkAddressing.ResolveLocalInterface(state.SelectedNetworkAdapterId, state.SelectedNetworkAddress);
    if (!ManagementAccess.Allows(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress, network))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Management access requires loopback or the selected production subnet and interface." });
        return;
    }
    // Browser requests may not use another web origin to mutate a trusted LAN server.
    if (context.Request.Headers.Origin.Count > 0
        && (!Uri.TryCreate(context.Request.Headers.Origin.ToString(), UriKind.Absolute, out var origin)
            || !string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    var gateway = context.RequestServices.GetRequiredService<DeviceUiGatewayService>();
    if (gateway.TryGetSession(context, out var session) && session is not null)
    {
        await gateway.ProxyAsync(context, session, context.RequestAborted);
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    product = "NDI Job Configurator",
    version = BuildIdentity.Version,
    channel = BuildIdentity.ReleaseChannel
}));
app.MapGet("/api/pc-onboarding/profile", async (AppStateStore store) =>
{
    var state = await store.ReadAsync();
    if (state.LastJob is null)
        return Results.Conflict(new { error = "This Job Configurator has no active job to join." });
    return Results.Ok(new
    {
        product = "NDI Job Configurator",
        version = BuildIdentity.Version,
        channel = BuildIdentity.ReleaseChannel,
        jobName = state.LastJob.JobName,
        ndiDiscoveryServerIp = state.LastJob.NdiDiscoveryServerIp,
        serverAddress = state.SelectedNetworkAddress
    });
});
app.MapGet("/api/pc-onboarding/configuration/{endpointId}", async (
    string endpointId,
    HttpContext context,
    WindowsPcRemoteOnboardingService remoteOnboarding) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress?.IsIPv4MappedToIPv6 == true) remoteAddress = remoteAddress.MapToIPv4();
    if (remoteAddress?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        return Results.Json(
            new { error = "The pending configuration is available only to its discovered IPv4 endpoint." },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var configuration = await remoteOnboarding.GetConfigurationAsync(endpointId, remoteAddress.ToString(), context.Request.Query["attemptId"].ToString());
        object? network = configuration.Network is null
            ? null
            : configuration.Network.Mode == "static"
                ? new
                {
                    configuration.Network.AdapterId,
                    configuration.Network.Mode,
                    configuration.Network.Address,
                    configuration.Network.PrefixLength,
                    configuration.Network.DefaultGateway,
                    configuration.Network.DnsServers
                }
                : new
                {
                    configuration.Network.AdapterId,
                    configuration.Network.Mode
                };
        return Results.Json(new
        {
            configuration.SchemaVersion,
            configuration.Product,
            configuration.EndpointId,
            configuration.JobName,
            configuration.NdiDiscoveryServerIp,
            configuration.AttemptId,
            configuration.JobId,
            configuration.JobRevision,
            configuration.RequiresFinalConfirmation,
            network
        });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/pc-onboarding/register", async (
    WindowsPcRegistration registration,
    HttpContext context,
    AppStateStore store,
    WindowsPcAgentService agents,
    WindowsPcRemoteOnboardingService remoteOnboarding) =>
{
    static string? RemoteIpv4(HttpContext httpContext)
    {
        var remote = httpContext.Connection.RemoteIpAddress;
        if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
        return remote?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? remote.ToString()
            : null;
    }

    var remoteAddress = RemoteIpv4(context);
    if (remoteAddress is null || IPAddress.IsLoopback(IPAddress.Parse(remoteAddress)))
        return Results.BadRequest(new { error = "Remote Windows PC registration must come from the selected LAN." });
    if (!string.Equals(remoteAddress, registration.Address, StringComparison.Ordinal))
        return Results.BadRequest(new { error = $"Registration address {registration.Address} does not match the connecting PC address {remoteAddress}." });
    var currentState = await store.ReadAsync();
    var selectedNetwork = NetworkAddressing.ResolveLocalInterface(
        currentState.SelectedNetworkAdapterId,
        currentState.SelectedNetworkAddress);
    if (selectedNetwork is null || !NetworkAddressing.Contains(
            IPAddress.Parse(remoteAddress),
            IPAddress.Parse(selectedNetwork.Address),
            selectedNetwork.PrefixLength))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!Guid.TryParse(registration.EndpointId, out _))
        return Results.BadRequest(new { error = "The Windows PC endpoint identifier is invalid." });
    if (registration.Address == selectedNetwork.Address || (currentState.WindowsPcs ?? []).Any(pc => pc.IsServerPc
            && pc.EndpointId.Equals(registration.EndpointId, StringComparison.OrdinalIgnoreCase)))
        return Results.Conflict(new { error = "This server PC must be onboarded through its installed local component." });
    if (string.IsNullOrWhiteSpace(registration.Hostname) || registration.Hostname.Length > 63)
        return Results.BadRequest(new { error = "The Windows PC hostname is required and must be at most 63 characters." });
    if (registration.PrefixLength is < 1 or > 30)
        return Results.BadRequest(new { error = "The Windows PC network prefix must be between /1 and /30." });
    if (!registration.PreferredInterfaceConfigured)
        return Results.BadRequest(new { error = "Apply the preferred NDI interface before registering this PC." });
    if (string.IsNullOrWhiteSpace(registration.AdapterName) || registration.AdapterName.Length > 256)
        return Results.BadRequest(new { error = "The Windows PC adapter name is required and must be at most 256 characters." });
    if (string.IsNullOrWhiteSpace(registration.NdiToolsVersion) || registration.NdiToolsVersion.Length > 40
        || string.IsNullOrWhiteSpace(registration.UtilityVersion) || registration.UtilityVersion.Length > 40)
        return Results.BadRequest(new { error = "The NDI Tools and onboarding utility versions are required." });
    if (registration.OperatingSystemVersion?.Length > 128)
        return Results.BadRequest(new { error = "The Windows operating-system version must be at most 128 characters." });
    if (!string.Equals(registration.EulaVersion, "1.0", StringComparison.Ordinal))
        return Results.BadRequest(new { error = "The current NDI Job Configurator EULA must be accepted." });

    var now = DateTimeOffset.UtcNow;
    var agent = agents.Snapshot().FirstOrDefault(item =>
        string.Equals(item.EndpointId, registration.EndpointId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.Address, registration.Address, StringComparison.Ordinal));
    var endpoint = new WindowsPcEndpoint(
        registration.EndpointId,
        registration.Hostname.Trim(),
        registration.Address,
        registration.AdapterName.Trim(),
        registration.PrefixLength,
        registration.PreferredInterfaceConfigured,
        registration.NdiToolsVersion.Trim(),
        registration.UtilityVersion.Trim(),
        registration.EulaVersion,
        now,
        now,
        "onboarded",
        LastConnectivityCheckUtc: agent is null ? null : now,
        ConsecutiveConnectivityFailures: 0,
        ConnectivityStatus: agent is null ? "unknown" : "online",
        OperatingSystemVersion: string.IsNullOrWhiteSpace(registration.OperatingSystemVersion)
            ? null
            : registration.OperatingSystemVersion.Trim(),
        AgentSchemaVersion: agent is null ? null : 1,
        AgentVersion: agent?.AgentVersion,
        AgentCapabilities: agent?.Capabilities,
        AgentUptimeSeconds: agent?.AgentUptimeSeconds,
        MachineUptimeSeconds: agent?.MachineUptimeSeconds,
        PhysicalMemoryTotalBytes: agent?.PhysicalMemoryTotalBytes,
        PhysicalMemoryAvailableBytes: agent?.PhysicalMemoryAvailableBytes,
        SystemDriveTotalBytes: agent?.SystemDriveTotalBytes,
        SystemDriveFreeBytes: agent?.SystemDriveFreeBytes,
        AgentObservedUtc: agent?.ObservedUtc,
        RegistrationAttemptId: registration.AttemptId,
        RegistrationJobId: registration.JobId,
        RegistrationJobRevision: registration.JobRevision);
    AppState state;
    try
    {
        state = await remoteOnboarding.RegisterAsync(registration, endpoint);
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    remoteOnboarding.RecordRegistration(endpoint, registration.AttemptId!);
    return Results.Ok(new
    {
        status = "awaiting-confirmation",
        endpoint,
        jobName = state.LastJob!.JobName
    });
});
app.MapPost("/api/pc-onboarding/outcome", async (
    WindowsPcOnboardingOutcome outcome, HttpContext context, WindowsPcRemoteOnboardingService remoteOnboarding) =>
{
    var source = context.Connection.RemoteIpAddress;
    if (source?.IsIPv4MappedToIPv6 == true) source = source.MapToIPv4();
    if (source is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
    try { return Results.Ok(await remoteOnboarding.ReportOutcomeAsync(outcome, source.ToString())); }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapDelete("/api/pc-onboarding/{endpointId}", async (
    string endpointId,
    AppStateStore store) =>
{
    if (!Guid.TryParse(endpointId, out _))
        return Results.BadRequest(new { error = "The Windows PC endpoint identifier is invalid." });

    WindowsPcEndpoint? removed = null;
    var state = await store.UpdateAsync(current =>
    {
        var endpoints = current.WindowsPcs ?? [];
        removed = endpoints.FirstOrDefault(item =>
            string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
        if (removed is null) return current;
        var remainingAssignments = current.Multicast?.Assignments
            .Where(assignment => !string.Equals(
                assignment.EndpointId,
                endpointId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return current with
        {
            WindowsPcs = endpoints
                .Where(item => !string.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            Multicast = current.Multicast is null
                ? null
                : remainingAssignments is []
                    ? null
                : current.Multicast with
                {
                    Assignments = remainingAssignments!
                }
        };
    });
    if (removed is null)
        return Results.NotFound(new { error = $"Windows endpoint '{endpointId}' was not found." });
    return Results.Ok(new
    {
        status = "removed",
        endpointId = removed.EndpointId,
        hostname = removed.Hostname,
        remaining = state.WindowsPcs?.Count ?? 0
    });
});
app.MapGet("/api/pc-agents", (WindowsPcAgentService agents, AppStateStore store, WindowsPcRemoteOnboardingService remoteOnboarding) =>
{
    return ReadAgentsAsync();

    async Task<IResult> ReadAgentsAsync()
    {
        var state = await store.ReadAsync();
        var registered = (state.WindowsPcs ?? [])
            .Select(endpoint => endpoint.EndpointId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Results.Ok(agents.Snapshot().Select(agent => new
        {
            product = WindowsPcAgentService.ProductName,
            agent.EndpointId,
            agent.Hostname,
            agent.Address,
            agent.PrefixLength,
            agent.ApiPort,
            agent.AgentVersion,
            agent.Capabilities,
            agent.Status,
            agent.OperatingSystemVersion,
            agent.AdapterId,
            agent.AdapterName,
            agent.NetworkConfiguration,
            agent.MulticastConfiguration,
            agent.NdiToolsInstalled,
            agent.NdiToolsVersion,
            agent.AgentUptimeSeconds,
            agent.MachineUptimeSeconds,
            agent.PhysicalMemoryTotalBytes,
            agent.PhysicalMemoryAvailableBytes,
            agent.SystemDriveTotalBytes,
            agent.SystemDriveFreeBytes,
            agent.Memberships,
            agent.ObservedUtc,
            agent.LastDiscoveredUtc,
            remoteOnboarding = remoteOnboarding.Status(agent.EndpointId, state),
            registered = registered.Contains(agent.EndpointId)
        }));
    }
});
app.MapPost("/api/pc-agents/discover", async (WindowsPcAgentService agents, CancellationToken ct) =>
{
    await agents.DiscoverAsync(ct);
    return Results.Ok(new { status = "completed", count = agents.Snapshot().Count });
});
app.MapGet("/api/pc-agents/{endpointId}/onboarding/status", async (
    string endpointId,
    WindowsPcRemoteOnboardingService remoteOnboarding,
    AppStateStore store) =>
{
    if (!Guid.TryParse(endpointId, out _))
        return Results.BadRequest(new { error = "The Windows PC endpoint identifier is invalid." });
    var status = remoteOnboarding.Status(endpointId, await store.ReadAsync());
    return status is null
        ? Results.NotFound(new { error = "No recent remote onboarding operation exists for this endpoint." })
        : Results.Ok(status);
});
app.MapPost("/api/pc-agents/{endpointId}/onboarding/open", async (
    string endpointId,
    WindowsPcRemoteOnboardingRequest request,
    HttpContext context,
    WindowsPcAgentService agents,
    WindowsPcRemoteOnboardingService remoteOnboarding,
    CancellationToken ct) =>
{
    if (!Guid.TryParse(endpointId, out _))
        return Results.BadRequest(new { error = "The Windows PC endpoint identifier is invalid." });
    var requestedBy = context.Connection.RemoteIpAddress;
    if (requestedBy?.IsIPv4MappedToIPv6 == true) requestedBy = requestedBy.MapToIPv4();
    if (requestedBy is not null && IPAddress.IsLoopback(requestedBy)) requestedBy = IPAddress.Loopback;
    if (requestedBy?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        return Results.Json(new { error = "Remote onboarding must be staged from IPv4 on the selected production network." }, statusCode: StatusCodes.Status403Forbidden);
    string? attemptId = null;
    try
    {
        var staged = await remoteOnboarding.StageAsync(endpointId, request, requestedBy.ToString());
        attemptId = staged.AttemptId;
        remoteOnboarding.RecordApprovalRequestStarted(endpointId, attemptId);
        var status = await agents.OpenOnboardingAsync(endpointId, ct, staged.AttemptId);
        remoteOnboarding.RecordAgentResponse(endpointId, status, attemptId);
        return status switch
        {
            System.Net.HttpStatusCode.Accepted => Results.Accepted(value: remoteOnboarding.Status(endpointId)),
            System.Net.HttpStatusCode.Forbidden => Results.Json(new { error = "The endpoint user denied onboarding. Request a new attempt for fresh local approval." }, statusCode: StatusCodes.Status403Forbidden),
            System.Net.HttpStatusCode.BadRequest => Results.BadRequest(new { error = $"The {WindowsPcAgentService.ProductName} rejected the onboarding request." }),
            _ => Results.Problem($"{WindowsPcAgentService.ProductName} returned HTTP {(int)status}.", statusCode: StatusCodes.Status502BadGateway)
        };
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (NotSupportedException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        remoteOnboarding.RecordFailure(endpointId, "The endpoint did not answer the local onboarding confirmation within 60 seconds.", attemptId);
        return Results.Problem("The endpoint did not answer the local onboarding confirmation within 60 seconds.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (HttpRequestException ex)
    {
        remoteOnboarding.RecordFailure(endpointId, $"The {WindowsPcAgentService.ProductName} could not be reached for local approval.", attemptId);
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapGet("/api/system/info", async (GitHubUpdateService updates, AppStateStore store) =>
{
    var state = await store.ReadAsync();
    return Results.Ok(updates.GetSystemInformation(state.UpdateChannel));
});
app.MapGet("/api/system/diagnostics", (HttpContext context, DiagnosticsService diagnostics) =>
{
    if (context.Connection.RemoteIpAddress is not { } remoteIp || !IPAddress.IsLoopback(remoteIp))
        return Results.Json(
            new { error = "Diagnostics can only be downloaded from the setup PC using localhost." },
            statusCode: StatusCodes.Status403Forbidden);
    app.Logger.LogInformation("Creating a local diagnostics package");
    var fileName = $"NDI-Job-Configurator-Diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip";
    return Results.File(diagnostics.CreateArchive(), "application/zip", fileName);
});
app.MapPut("/api/system/update/channel", async (UpdateChannelSelection selection, AppStateStore store) =>
{
    if (!Enum.IsDefined(selection.Channel))
        return Results.BadRequest(new { error = "Select either the Main or Development release channel." });
    var state = await store.UpdateAsync(current => current with { UpdateChannel = selection.Channel });
    return Results.Ok(new { channel = state.UpdateChannel });
});
app.MapGet("/api/system/update", async (GitHubUpdateService updates, AppStateStore store, CancellationToken ct) =>
{
    try
    {
        var state = await store.ReadAsync();
        return Results.Ok(await updates.CheckAsync(state.UpdateChannel, ct));
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});
app.MapPost("/api/system/update/install", async (GitHubUpdateService updates, AppStateStore store, CancellationToken ct) =>
{
    try
    {
        var state = await store.ReadAsync();
        return Results.Accepted(value: await updates.DownloadAndLaunchAsync(state.UpdateChannel, ct));
    }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
    catch (IOException ex) { return Results.Problem(ex.Message, statusCode: 500); }
});
app.MapGet("/license", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "LICENSE.md"), "text/markdown; charset=utf-8"));
app.MapGet("/third-party-notices", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "THIRD-PARTY-NOTICES", "README.md"), "text/markdown; charset=utf-8"));
app.MapGet("/api/network/subnets", async (AppStateStore store) =>
{
    var state = await store.ReadAsync();
    var selected = NetworkAddressing.ResolveLocalInterface(
        state.SelectedNetworkAdapterId,
        state.SelectedNetworkAddress);
    return Results.Ok(selected is null ? [] : new[] { NetworkAddressing.GetScanCidr(selected) });
});
app.MapGet("/api/network/interfaces", () => Results.Ok(NetworkAddressing.GetLocalInterfaces()));
app.MapGet("/api/pc-onboarding/local", (ILocalPcOnboarding localPc) => Results.Ok(localPc.Status));
app.MapPost("/api/pc-onboarding/diagnostics", async (HttpContext context, OnboardingDiagnosticsStore diagnostics,
    WindowsPcAgentService agents) =>
{
    if (context.Request.ContentLength > OnboardingDiagnosticsStore.MaximumReportBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    try
    {
        // Bound streamed/chunked requests too, before deserialization or disk writes.
        var bytes = new byte[OnboardingDiagnosticsStore.MaximumReportBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), context.RequestAborted);
            if (read == 0) break;
            count += read;
        }
        if (count > OnboardingDiagnosticsStore.MaximumReportBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        var report = JsonSerializer.Deserialize<OnboardingFailureReport>(bytes.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("A failure report is required.");
        var source = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "";
        var saved = diagnostics.SaveRemote(report, source,
            (endpoint, address) => agents.Snapshot().Any(a => a.EndpointId == endpoint && a.Address == address));
        return Results.Ok(new { saved.ReportId, saved.ExpiresUtc });
    }
    catch (UnauthorizedAccessException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
    catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
    { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/pc-onboarding/diagnostics", (HttpContext context, string? endpointId, OnboardingDiagnosticsStore diagnostics) =>
    context.Connection.RemoteIpAddress is { } source && IPAddress.IsLoopback(source)
        ? Results.Ok(new { retentionDays = OnboardingDiagnosticsStore.RetentionDays, reports = diagnostics.List(endpointId) })
        : Results.Json(new { error = "Internal diagnostics are available from localhost on the server." }, statusCode: 403));
app.MapGet("/api/pc-onboarding/diagnostics/{reportId:guid}", (HttpContext context, string reportId, OnboardingDiagnosticsStore diagnostics) =>
{
    if (context.Connection.RemoteIpAddress is not { } source || !IPAddress.IsLoopback(source))
        return Results.Json(new { error = "Internal diagnostics are available from localhost on the server." }, statusCode: 403);
    context.Response.Headers.CacheControl = "no-store";
    return diagnostics.ReportText(reportId) is { } report ? Results.Text(report, "text/plain; charset=utf-8")
        : Results.NotFound(new { error = "This onboarding report is unavailable or its seven-day retention period has expired." });
});
app.MapPost("/api/pc-onboarding/local", async (OnboardingService onboarding, AppStateStore store,
    OnboardingDiagnosticsStore diagnostics, CancellationToken ct) =>
{
    try
    {
        var endpoint = await onboarding.ReapplyServerPcAsync(ct);
        return Results.Ok(new { endpoint });
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException
        or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    {
        var reportId = (ex as LocalPcOnboardingException)?.ReportId;
        if (ex is not LocalPcOnboardingException)
        {
            // Also retain failures that happen before Setup starts (for example a missing job/adapter).
            try
            {
                var current = await store.ReadAsync();
                var attemptId = Guid.NewGuid().ToString("D");
                diagnostics.Register(new(current.WindowsPcs?.FirstOrDefault(pc => pc.IsServerPc)?.EndpointId ?? "", attemptId,
                    Environment.MachineName, current.JobId, current.JobRevision, current.LastJob?.JobName ?? "No active job",
                    current.SelectedNetworkAddress ?? "", null, current.SelectedNetworkAdapterId ?? "", true,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));
                reportId = diagnostics.SaveLocal(attemptId, ex).ReportId;
            }
            catch (Exception saveError) when (saveError is IOException or UnauthorizedAccessException or ArgumentException)
            { app.Logger.LogError(saveError, "Could not store local onboarding request failure"); }
        }
        return Results.Conflict(new { error = ex.Message, reportId });
    }
});
app.MapPut("/api/network/selection", async (NetworkAdapterSelection selection, AppStateStore store) =>
{
    var selected = NetworkAddressing.ResolveLocalInterface(selection.AdapterId, selection.Address);
    if (selected is null) return Results.BadRequest(new { error = "Select an active IPv4 network adapter." });
    await store.UpdateAsync(state => state with
    {
        SelectedNetworkAdapterId = selected.Id,
        SelectedNetworkAddress = selected.Address
    });
    return Results.Ok(new
    {
        selected.Id,
        selected.Name,
        selected.Description,
        selected.Address,
        selected.PrefixLength,
        selected.Type,
        ScanCidr = NetworkAddressing.GetScanCidr(selected)
    });
});
app.MapGet("/api/state", async (AppStateStore store) => Results.Ok(await store.ReadAsync()));
app.MapGet("/api/devices", async (AppStateStore store) => Results.Ok((await store.ReadAsync()).Devices));
app.MapPost("/api/devices/{id}/ui-session", async (
    string id,
    HttpContext context,
    AppStateStore store,
    DeviceUiGatewayService gateway,
    CancellationToken ct) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress?.IsIPv4MappedToIPv6 == true) remoteAddress = remoteAddress.MapToIPv4();
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        return Results.BadRequest(new { error = "Automatic Kiloview login is available from the Job Configurator host PC only." });
    var device = (await store.ReadAsync()).Devices.FirstOrDefault(candidate => candidate.Id == id && candidate.IsOnboarded);
    if (device is null) return Results.NotFound(new { error = $"Device '{id}' was not found." });
    try
    {
        return Results.Ok(new { url = await gateway.CreateAsync(device, servicePort, ct) });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapGet("/api/devices/{id}/ui", async (
    string id,
    HttpContext context,
    AppStateStore store,
    DeviceUiGatewayService gateway,
    CancellationToken ct) =>
{
    var device = (await store.ReadAsync()).Devices.FirstOrDefault(candidate => candidate.Id == id && candidate.IsOnboarded);
    if (device is null) return Results.NotFound(new { error = $"Device '{id}' was not found." });
    var directUrl = $"http://{device.IpAddress}{(device.WebPort == 80 ? "" : $":{device.WebPort}")}";
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress?.IsIPv4MappedToIPv6 == true) remoteAddress = remoteAddress.MapToIPv4();
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress)) return Results.Redirect(directUrl);
    try
    {
        return Results.Redirect(await gateway.CreateAsync(device, servicePort, ct));
    }
    catch (InvalidOperationException)
    {
        return Results.Redirect(directUrl);
    }
});
app.MapGet("/api/devices/{id}/thumbnail", async (string id, EncoderThumbnailService thumbnails, HttpResponse response, CancellationToken ct) =>
{
    try
    {
        var thumbnail = await thumbnails.GetAsync(id, ct);
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Kiloview-Preview"] = thumbnail.Live ? "live" : thumbnail.Warning ? "warning" : "unavailable";
        response.Headers["X-Kiloview-Preview-Failures"] = thumbnail.ConsecutiveFailures.ToString();
        return Results.File(thumbnail.Bytes, "image/bmp");
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/kilolink/credentials", (string serverIp, HttpContext context, KiloLinkCredentialStore credentials) =>
{
    try
    {
        var status = credentials.GetStatus(serverIp);
        var canReveal = context.Connection.RemoteIpAddress is { } remoteIp && IPAddress.IsLoopback(remoteIp);
        return Results.Ok(new { status.Stored, status.Username, CanReveal = canReveal });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/kilolink/credentials/reveal", (string serverIp, HttpContext context, HttpResponse response, KiloLinkCredentialStore credentials) =>
{
    response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    response.Headers.Pragma = "no-cache";
    response.Headers.Expires = "0";
    if (context.Connection.RemoteIpAddress is not { } remoteIp || !IPAddress.IsLoopback(remoteIp))
        return Results.Json(
            new { error = "Stored passwords can only be viewed from the setup PC using localhost." },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var credential = credentials.GetStoredCredential(serverIp);
        return Results.Ok(new { credential.Username, credential.Password });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/kilolink/discover", async (
    int? webPort,
    KiloLinkServerClient client,
    AppStateStore store,
    CancellationToken ct) =>
{
    try
    {
        var state = await store.ReadAsync();
        var network = NetworkAddressing.ResolveLocalInterface(
            state.SelectedNetworkAdapterId,
            state.SelectedNetworkAddress)
            ?? throw new ArgumentException("Select an active network adapter before searching for KiloLink Server.");
        return Results.Ok(await client.DiscoverAsync(webPort ?? 80, network, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/ndi/discover", async (
    int? port,
    NdiDiscoveryServerClient client,
    AppStateStore store,
    CancellationToken ct) =>
{
    try
    {
        var state = await store.ReadAsync();
        var network = NetworkAddressing.ResolveLocalInterface(
            state.SelectedNetworkAdapterId,
            state.SelectedNetworkAddress)
            ?? throw new ArgumentException("Select an active network adapter before searching for an NDI Discovery Server.");
        return Results.Ok(await client.DiscoverAsync(port ?? NdiDiscoveryServerClient.DefaultPort, network, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/kilolink/test", async (KiloLinkConnectionRequest request, KiloLinkConnectionService connections, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await connections.ConnectAsync(request, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (System.ComponentModel.Win32Exception ex) { return Results.Problem(ex.Message, statusCode: 500); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/api/discovery", async (DiscoveryRequest request, NetworkDiscovery discovery, CancellationToken ct) =>
{
    try { return Results.Ok(await discovery.DiscoverAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/onboarding/plan", async (OnboardingRequest request, OnboardingService onboarding, CancellationToken ct) =>
{
    try { return Results.Ok(await onboarding.BuildPlanAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/onboarding/run", async (OnboardingRunRequest request, OnboardingService onboarding, CancellationToken ct) =>
{
    try { return Results.Accepted(value: await onboarding.StartAsync(request.PlanId, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/onboarding/progress", (OnboardingService onboarding) => Results.Ok(onboarding.Progress));
app.MapPost("/api/onboarding/identify", async (OnboardingService onboarding, CancellationToken ct) =>
    Results.Ok(await onboarding.PrepareDecoderIdentificationAsync(ct)));

app.MapPost("/api/multicast/plan", async (MulticastSetupRequest request, MulticastService multicast, CancellationToken ct) =>
{
    try { return Results.Ok(await multicast.BuildPlanAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/multicast/apply", async (MulticastConfiguration plan, MulticastService multicast, CancellationToken ct) =>
{
    try { return Results.Ok(await multicast.ApplyAsync(plan, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/multicast/revert", async (MulticastService multicast, CancellationToken ct) =>
{
    try { return Results.Ok(await multicast.RevertToUnicastAsync(ct)); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/firmware/stage", async (HttpRequest request, FirmwareService firmware, CancellationToken ct) =>
{
    try
    {
        var models = request.Query["models"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Results.Ok(await firmware.StageMultipartAsync(request, models, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidDataException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
}).WithMetadata(new RequestSizeLimitAttribute(FirmwareService.MaximumRequestBytes));
app.MapPost("/api/firmware/start", async (FirmwareService firmware, CancellationToken ct) =>
{
    try { return Results.Ok(await firmware.StartAsync(ct)); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/devices/{id}/role", async (string id, RoleUpdate update, OnboardingService onboarding, CancellationToken ct) =>
{
    try { return Results.Ok(await onboarding.SetRoleAsync(id, update.Role, ct)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DeviceApiException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/api/devices/{id}/identity", async (string id, IdentityUpdate update, OnboardingService onboarding, CancellationToken ct) =>
{
    try { return Results.Ok(await onboarding.SetIdentityAsync(id, update, ct)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (DeviceApiException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/api/teletools/{id}/start", async (string id, AppStateStore store, TeleToolFleetService teleTools, CancellationToken ct) =>
{
    try
    {
        var device = (await store.ReadAsync()).Devices.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException($"Device '{id}' was not found.");
        var updated = await teleTools.StartAsync(device, ct);
        await store.UpdateAsync(state => state with { Devices = state.Devices.Select(candidate => candidate.Id == id ? updated : candidate).ToArray() });
        return Results.Ok(updated);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/api/teletools/{id}/stop", async (string id, AppStateStore store, TeleToolFleetService teleTools, CancellationToken ct) =>
{
    try
    {
        var device = (await store.ReadAsync()).Devices.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException($"Device '{id}' was not found.");
        var updated = await teleTools.StopAsync(device, ct);
        await store.UpdateAsync(state => state with { Devices = state.Devices.Select(candidate => candidate.Id == id ? updated : candidate).ToArray() });
        return Results.Ok(updated);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapDelete("/api/teletools/{id}", async (
    string id,
    AppStateStore store,
    TeleToolFleetService teleTools,
    EncoderThumbnailService thumbnails,
    CancellationToken ct) =>
{
    try
    {
        var device = (await store.ReadAsync()).Devices.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException($"Device '{id}' was not found.");
        var result = await teleTools.RemoveAsync(device, ct);
        thumbnails.Forget(id);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapGet("/api/devices/{id}/hdmi-input", async (string id, OnboardingService onboarding, CancellationToken ct) =>
{
    try { return Results.Ok(await onboarding.ProbeEncoderInputAsync(id, ct)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (DeviceApiException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapDelete("/api/devices/{id}", async (
    string id,
    OnboardingService onboarding,
    CancellationToken ct) =>
{
    try { return Results.Ok(await onboarding.RemoveKiloviewAsync(id, ct)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/api/onboarding/complete", async (OnboardingService onboarding, CancellationToken ct) =>
    Results.Ok(await onboarding.CompleteAsync(ct)));

app.MapPost("/api/simulation/reset", async (AppStateStore store, EncoderThumbnailService thumbnails) =>
{
    await store.UpdateAsync(s => s with { Devices = [], LastJob = null });
    thumbnails.ForgetAll();
    return Results.Ok();
});

if (args.Contains("--open-browser", StringComparer.OrdinalIgnoreCase))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Process.Start(new ProcessStartInfo($"http://localhost:{servicePort}") { UseShellExecute = true }); }
        catch { /* The web service is still usable if no browser is registered. */ }
    });
}

app.Map("/api/{**path}", () => Results.NotFound(new { error = "API resource not found." }));
app.MapFallbackToFile("index.html");
var systemTray = app.Services.GetRequiredService<SystemTrayService>();
try
{
    app.Logger.LogInformation(
        "Starting NDI Job Configurator {Version} ({Channel}) on port {Port}; LAN access: {LanAccess}",
        BuildIdentity.Version,
        BuildIdentity.ReleaseChannel,
        servicePort,
        lanAccess);
    await app.RunAsync();
}
finally
{
    app.Logger.LogInformation("NDI Job Configurator is stopping");
    if (ownsInstanceSemaphore) instanceSemaphore.Release();
}

if (systemTray.RestartRequested && Environment.ProcessPath is { } executablePath)
{
    var restart = new ProcessStartInfo
    {
        FileName = executablePath,
        UseShellExecute = false,
        WorkingDirectory = app.Environment.ContentRootPath
    };
    if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        restart.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
    if (lanAccess) restart.ArgumentList.Add("--lan");
    Process.Start(restart);
}
