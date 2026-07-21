using System.Diagnostics;
using System.Text.Json.Serialization;
using KiloviewSetup.Core;
using KiloviewSetup.Devices;

var servicePort = int.TryParse(Environment.GetEnvironmentVariable("KILOVIEW_SERVICE_PORT"), out var configuredPort)
    && configuredPort is >= 1024 and <= 65535 ? configuredPort : 8091;
using var instanceMutex = new Mutex(true, $"Local\\KiloviewJobConfigurator-{servicePort}", out var ownsInstanceMutex);
if (!ownsInstanceMutex)
{
    try { Process.Start(new ProcessStartInfo($"http://localhost:{servicePort}") { UseShellExecute = true }); }
    catch { /* The existing tray process remains available even if no browser is registered. */ }
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddDebug();
builder.WebHost.UseUrls($"http://127.0.0.1:{servicePort}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});
builder.Services.AddSingleton<AppStateStore>();
builder.Services.AddSingleton<KiloLinkCredentialStore>();
builder.Services.AddSingleton<KiloLinkServerClient>();
builder.Services.AddSingleton<NdiTitleCardService>();
builder.Services.AddSingleton<EncoderThumbnailService>();
builder.Services.AddSingleton<FirmwareService>();
builder.Services.AddSingleton<TeleToolFleetIdentity>();
builder.Services.AddSingleton<TeleToolFleetService>();
builder.Services.AddSingleton<DeviceClientFactory>();
builder.Services.AddSingleton<NetworkDiscovery>();
builder.Services.AddSingleton<OnboardingService>();
builder.Services.AddSingleton<SystemTrayService>();
builder.Services.AddHostedService(services => services.GetRequiredService<SystemTrayService>());
builder.Services.AddHttpClient<GitHubUpdateService>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Kiloview-Job-Configurator");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    client.Timeout = TimeSpan.FromMinutes(15);
});
builder.Services.AddHttpClient("TeleTool", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHostedService<DeviceMonitor>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version = BuildIdentity.Version,
    channel = BuildIdentity.ReleaseChannel
}));
app.MapGet("/api/system/info", async (GitHubUpdateService updates, AppStateStore store) =>
{
    var state = await store.ReadAsync();
    return Results.Ok(updates.GetSystemInformation(state.UpdateChannel));
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
app.MapGet("/api/network/subnets", () => Results.Ok(NetworkAddressing.GetLocalScanCidrs()));
app.MapGet("/api/state", async (AppStateStore store) => Results.Ok(await store.ReadAsync()));
app.MapGet("/api/devices", async (AppStateStore store) => Results.Ok((await store.ReadAsync()).Devices));
app.MapGet("/api/devices/{id}/thumbnail", async (string id, EncoderThumbnailService thumbnails, HttpResponse response, CancellationToken ct) =>
{
    try
    {
        var thumbnail = await thumbnails.GetAsync(id, ct);
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Kiloview-Preview"] = thumbnail.Live ? "live" : "unavailable";
        return Results.File(thumbnail.Bytes, "image/bmp");
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/kilolink/credentials", (string serverIp, KiloLinkCredentialStore credentials) =>
{
    try { return Results.Ok(credentials.GetStatus(serverIp)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/kilolink/discover", async (int? webPort, KiloLinkServerClient client, CancellationToken ct) =>
{
    try { return Results.Ok(await client.DiscoverAsync(webPort ?? 80, ct)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/kilolink/test", async (KiloLinkConnectionRequest request, KiloLinkCredentialStore credentials, KiloLinkServerClient client, CancellationToken ct) =>
{
    try
    {
        var credential = credentials.ResolveAndStore(request.ServerIp, request.Username, request.Password);
        return Results.Ok(await client.TestAsync(request.ServerIp, request.WebPort, credential, ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
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

app.MapPost("/api/onboarding/run", (OnboardingPlan plan, OnboardingService onboarding) =>
{
    try { return Results.Accepted(value: onboarding.Start(plan)); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapGet("/api/onboarding/progress", (OnboardingService onboarding) => Results.Ok(onboarding.Progress));
app.MapPost("/api/onboarding/identify", async (OnboardingService onboarding, CancellationToken ct) =>
    Results.Ok(await onboarding.PrepareDecoderIdentificationAsync(ct)));

app.MapPost("/api/firmware/stage", async (HttpRequest request, FirmwareService firmware, CancellationToken ct) =>
{
    try
    {
        var form = await request.ReadFormAsync(ct);
        return Results.Ok(await firmware.StageAsync(form.Files.GetFile("n6Firmware"), form.Files.GetFile("n60Firmware"), ct));
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});
app.MapPost("/api/firmware/start", async (FirmwareService firmware, CancellationToken ct) =>
{
    try { return Results.Ok(await firmware.StartAsync(ct)); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapPost("/api/devices/{id}/role", async (string id, RoleUpdate update, OnboardingService onboarding, CancellationToken ct) =>
{
    try { return Results.Ok(await onboarding.SetRoleAsync(id, update.Role, ct)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
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

app.MapDelete("/api/teletools/{id}", async (string id, AppStateStore store, TeleToolFleetService teleTools, CancellationToken ct) =>
{
    try
    {
        var device = (await store.ReadAsync()).Devices.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException($"Device '{id}' was not found.");
        return Results.Ok(await teleTools.RemoveAsync(device, ct));
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }
});

app.MapPost("/api/onboarding/complete", async (OnboardingService onboarding, CancellationToken ct) =>
    Results.Ok(await onboarding.CompleteAsync(ct)));

app.MapPost("/api/simulation/reset", async (AppStateStore store) =>
{
    await store.UpdateAsync(s => s with { Devices = [], LastJob = null });
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

app.MapFallbackToFile("index.html");
var systemTray = app.Services.GetRequiredService<SystemTrayService>();
try
{
    await app.RunAsync();
}
finally
{
    instanceMutex.ReleaseMutex();
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
    Process.Start(restart);
}
