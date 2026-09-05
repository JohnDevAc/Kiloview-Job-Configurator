using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NDIJobConfigurator.Core;

var root = Path.Combine(Path.GetTempPath(), "ndi-regression-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("NDI_JOB_CONFIGURATOR_DATA_DIR", root);
Environment.SetEnvironmentVariable("NDI_JOB_CONFIGURATOR_NDI_CONFIG_PATH", Path.Combine(root, "ndi.json"));
var environment = new TestEnvironment { ContentRootPath = root };
var store = new AppStateStore(environment, NullLogger<AppStateStore>.Instance);
var now = DateTimeOffset.UtcNow;
var passed = 0;
await Run("Remote polls survive state deserialization", RemotePoll);
await Run("Three missed Windows polls persist offline status", RemoteOffline);
await Run("Stale polls cannot replace configuration or credentials", StalePoll);
await Run("Fresh polls retain license acceptance", FreshPoll);
await Run("Clean onboarding validates readiness before deleting inventory", CleanPreflight);
await Run("Incremental plans allocate unique names for both families", UniqueNames);
await Run("Partial unicast reversions survive monitor passes", PartialRevert);
await Run("Normal multicast drift is still detected", MulticastDrift);
await Run("State cache is immutable and reloads external edits", StateCache);
await Run("State recovery still works with caching", StateRecovery);
await Run("Preview cache invalidates after state changes", PreviewCache);
await Run("Preview failures back off without hiding recovery", PreviewBackoff);
await Run("Gateway sessions work with localhost and LAN listeners", Gateway);
Console.WriteLine($"PASS: {passed} regression checks. Isolated data: {root}");

async Task Run(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

RemoteWindowsPcEndpoint Remote() => new(Guid.NewGuid().ToString(), "Review-PC", "192.0.2.25", "Test", 24,
    true, "6.0", "1.0", "test", now, now, "onboarded", ConnectivityStatus: "online",
    AgentCapabilities: new[] { "status-v1", "multicast-config-v1" });

async Task RemotePoll()
{
    await store.UpdateAsync(_ => new AppState([], RemoteWindowsPcs: [Remote()]));
    var snapshot = await store.ReadAsync();
    var current = await store.ReloadAsync();
    Check(!ReferenceEquals(current.RemoteWindowsPcs![0].AgentCapabilities, snapshot.RemoteWindowsPcs![0].AgentCapabilities), "Test must deserialize independent lists.");
    var updated = snapshot.RemoteWindowsPcs[0] with { AgentUptimeSeconds = 600, LastConnectivityCheckUtc = now };
    var result = Apply(current, [], [(snapshot.RemoteWindowsPcs[0], updated)]);
    Check(result.RemoteWindowsPcs![0].AgentUptimeSeconds == 600, "Successful poll was discarded.");
    var reRegistered = current.RemoteWindowsPcs[0] with { Address = "192.0.2.26" };
    var stale = Apply(current with { RemoteWindowsPcs = [reRegistered] }, [], [(snapshot.RemoteWindowsPcs[0], updated)]);
    Check(stale.RemoteWindowsPcs![0] == reRegistered, "Old poll replaced a new registration.");
}

async Task RemoteOffline()
{
    await store.UpdateAsync(_ => new AppState([], RemoteWindowsPcs: [Remote()]));
    var agents = new WindowsPcAgentService(store, null!, NullLogger<WindowsPcAgentService>.Instance);
    var monitor = new DeviceMonitor(store, null!, null!, agents, NullLogger<DeviceMonitor>.Instance);
    for (var index = 1; index <= 3; index++)
    {
        await InvokeTask(monitor, "PollAsync", CancellationToken.None);
        var endpoint = (await store.ReloadAsync()).RemoteWindowsPcs![0];
        Check(endpoint.ConsecutiveConnectivityFailures == index, "Missed poll counter did not advance.");
        Check(endpoint.ConnectivityStatus == (index == 3 ? "offline" : "stale"), "Incorrect connectivity transition.");
    }
}

Task StalePoll()
{
    var original = Device("old", "192.0.2.20");
    var current = original with { Credentials = new("admin", "ReviewJob2"), LicenseAccepted = true, Health = DeviceHealth.Configuring };
    var result = Apply(new AppState([current]), [(original, original with { Health = DeviceHealth.Online })], []);
    Check(result.Devices[0] == current, "Stale poll overwrote credentials or configuration state.");
    var multicastChanged = original with { MulticastConfigured = true, MulticastNetPrefix = "239.192.1.0" };
    result = Apply(new AppState([multicastChanged]), [(original, original with { Health = DeviceHealth.Offline })], []);
    Check(result.Devices[0] == multicastChanged, "Stale poll overwrote multicast configuration.");
    return Task.CompletedTask;
}

async Task FreshPoll()
{
    var original = Device("licensed", "192.0.2.20") with { LicenseAccepted = true };
    var result = Apply(new AppState([original]), [(original, original with { LicenseAccepted = false, LastSeenUtc = now.AddSeconds(1) })], []);
    Check(result.Devices[0].LicenseAccepted && result.Devices[0].LastSeenUtc == now.AddSeconds(1), "Readback cleared license metadata or dropped fresh telemetry.");
    var interrupted = original with { Health = DeviceHealth.Configuring };
    await store.UpdateAsync(_ => new AppState([interrupted]));
    var monitor = new DeviceMonitor(store, null!, null!, null!, NullLogger<DeviceMonitor>.Instance);
    await InvokeTask(monitor, "PollAsync", CancellationToken.None);
    Check((await store.ReadAsync()).Devices[0].Health == DeviceHealth.Online, "An interrupted configuration could not recover through monitoring.");
}

async Task CleanPreflight()
{
    var selected = Device("selected", "192.0.2.20", DeviceFamily.SimulatedTeleTool) with { IsStatic = true };
    var prior = Device("prior", "192.0.2.21") with { IsStatic = true, IsOnboarded = true };
    var before = await store.UpdateAsync(_ => new AppState([selected, prior], Job(), TeleToolManagerId: "retain-manager",
        SelectedNetworkAdapterId: "missing-test-adapter", SelectedNetworkAddress: "192.0.2.200", RemoteWindowsPcs: [Remote()]));
    using var cards = new NdiTitleCardService(store, NullLogger<NdiTitleCardService>.Instance);
    var onboarding = Onboarding(cards);
    var plan = new OnboardingPlan(Guid.NewGuid(), Settings([selected.Id]) with { CleanOnboarding = true },
        [new(selected.Id, selected.IpAddress, selected.IpAddress, "ReviewJob2-TT-001", DeviceRole.Encoder, true, selected.Family)], [], [], now.AddMinutes(15));
    var plans = (Dictionary<Guid, OnboardingPlan>)typeof(OnboardingService).GetField("_plans", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(onboarding)!;
    plans.Add(plan.PlanId, plan);
    await Throws<InvalidOperationException>(() => onboarding.StartAsync(plan.PlanId, CancellationToken.None));
    Check(ReferenceEquals(before, await store.ReadAsync()), "Readiness failure changed existing inventory.");
    Check(onboarding.Progress.Status == "idle", "Failed preflight started an onboarding run.");
}

async Task UniqueNames()
{
    var existing = Device("existing", "192.0.2.20") with { Hostname = "ReviewJob2-KV-001", IsStatic = true, IsOnboarded = true };
    var teleTool = existing with { Id = "existing-tt", IpAddress = "192.0.2.21", Hostname = "REVIEWJOB2-TT-001", Family = DeviceFamily.SimulatedTeleTool };
    var addition = existing with { Id = "addition", IpAddress = "192.0.2.22", Hostname = "Factory", IsOnboarded = false };
    var additionTt = addition with { Id = "addition-tt", IpAddress = "192.0.2.23", Family = DeviceFamily.SimulatedTeleTool };
    await store.UpdateAsync(_ => new AppState([existing, teleTool, addition, additionTt], Job()));
    using var cards = new NdiTitleCardService(store, NullLogger<NdiTitleCardService>.Instance);
    var onboarding = Onboarding(cards);
    var plan = await onboarding.BuildPlanAsync(Settings([addition.Id, additionTt.Id]) with { StaticEnd = "192.0.2.23" }, CancellationToken.None);
    Check(plan.Devices[0].Hostname == "ReviewJob2-KV-002" && plan.Devices[1].Hostname == "ReviewJob2-TT-002", "Plan reused existing names.");
    await store.UpdateAsync(state => state with { Devices = state.Devices.Append(existing with { Id = "collision", Hostname = plan.Devices[0].Hostname }).ToArray() });
    await Throws<InvalidOperationException>(() => InvokeTask(onboarding, "ValidatePlanAsync", plan, CancellationToken.None));
    await store.UpdateAsync(_ => new AppState([
        existing with { Hostname = "Review-Job2-KV-001" },
        teleTool with { Hostname = "Review-Job2-TT-001" }, addition, additionTt], Job() with { JobName = "Review-Job2" }));
    var sanitized = await onboarding.BuildPlanAsync(Settings([addition.Id, additionTt.Id]) with { JobName = "Review Job2", StaticEnd = "192.0.2.23" }, CancellationToken.None);
    Check(sanitized.Devices[0].Hostname == "Review-Job2-KV-002" && sanitized.Devices[1].Hostname == "Review-Job2-TT-002", "Sanitized job names reused existing hostnames.");
}

Task PartialRevert()
{
    var device = Device("reverted", "192.0.2.26", DeviceFamily.SimulatedTeleTool) with { Health = DeviceHealth.Online, IsOnboarded = true };
    var assignment = Assignment(device) with { Status = "unicast" };
    var configuration = Multicast([assignment, assignment with { EndpointId = "unreachable", Status = "error" }], "revert-partial");
    foreach (var status in new[] { "reverting", "revert-partial" })
    {
        var current = new AppState([device], Multicast: configuration with { Status = status });
        var result = Apply(current, [(device, device with { LastSeenUtc = now.AddSeconds(1) })], []);
        Check(result.Multicast!.Status == status && result.Multicast.Assignments[0].Status == "unicast", "Monitor changed partial revert results.");
        Check(result.Devices[0].MulticastLastError is null, "Unicast endpoint received a multicast error.");
    }
    return Task.CompletedTask;
}

Task MulticastDrift()
{
    var device = Device("drifted", "192.0.2.26", DeviceFamily.SimulatedTeleTool) with { Health = DeviceHealth.Online, IsOnboarded = true };
    var state = new AppState([device], Multicast: Multicast([Assignment(device)], "completed"));
    var result = Apply(state, [(device, device with { LastSeenUtc = now.AddSeconds(1) })], []);
    Check(result.Multicast!.Assignments[0].Status == "drifted" && result.Multicast.Status == "partial", "Expected multicast drift was ignored.");
    return Task.CompletedTask;
}

async Task StateCache()
{
    var input = new[] { Device("cached", "192.0.2.20") };
    var capabilities = new[] { "status-v1" };
    var saved = await store.UpdateAsync(_ => new AppState(input, RemoteWindowsPcs: [Remote() with { AgentCapabilities = capabilities }]));
    input[0] = input[0] with { Hostname = "Unexpected" };
    capabilities[0] = "Unexpected";
    Check(saved.Devices[0].Hostname == "cached" && saved.RemoteWindowsPcs![0].AgentCapabilities![0] == "status-v1", "Caller mutated cached collections.");
    Check(ReferenceEquals(saved, await store.ReadAsync()), "Unchanged state was reloaded.");
    await Throws<NotSupportedException>(() => { ((IList<ManagedDevice>)saved.Devices)[0] = input[0]; return Task.CompletedTask; });
    var file = Path.Combine(root, "state.json");
    await File.WriteAllTextAsync(file, "{\"devices\":[],\"teleToolManagerId\":\"externally-edited\"}");
    Check((await store.ReadAsync()).TeleToolManagerId == "externally-edited", "External edit did not invalidate cached state.");
    var stamp = File.GetLastWriteTimeUtc(file);
    await File.WriteAllTextAsync(file, "{\"devices\":[],\"teleToolManagerId\":\"externally-fixed!\"}");
    File.SetLastWriteTimeUtc(file, stamp);
    Check((await store.ReloadAsync()).TeleToolManagerId == "externally-fixed!", "Explicit reload failed.");
}

async Task StateRecovery()
{
    await store.UpdateAsync(_ => new AppState([Device("recoverable", "192.0.2.20")]));
    var file = Path.Combine(root, "state.json");
    await File.WriteAllTextAsync(file, "invalid JSON");
    Check((await store.ReadAsync()).Devices.Single().Id == "recoverable", "Corrupt state did not recover from backup.");
    File.Delete(file);
    Check((await store.ReadAsync()).Devices.Single().Id == "recoverable" && File.Exists(file), "Missing state did not recover from backup.");
}

async Task PreviewCache()
{
    var device = Device("preview", "192.0.2.20") with { Health = DeviceHealth.Online, IsOnboarded = true };
    await store.UpdateAsync(_ => new AppState([device]));
    using var thumbnails = new EncoderThumbnailService(store, NullLogger<EncoderThumbnailService>.Instance);
    var first = await thumbnails.GetAsync(device.Id, CancellationToken.None);
    Check(first.Live && ReferenceEquals(first, await thumbnails.GetAsync(device.Id, CancellationToken.None)), "Healthy preview was not cached.");
    await store.UpdateAsync(state => state with { Devices = [device with { Health = DeviceHealth.Offline }] });
    Check(!(await thumbnails.GetAsync(device.Id, CancellationToken.None)).Live, "Offline transition reused a live preview.");
    await store.UpdateAsync(state => state with { Devices = [device] });
    Check((await thumbnails.GetAsync(device.Id, CancellationToken.None)).Live, "Recovery did not invalidate the unavailable preview.");
}

async Task PreviewBackoff()
{
    var device = Device("backoff", "192.0.2.20") with { Health = DeviceHealth.Online, IsOnboarded = true };
    await store.UpdateAsync(_ => new AppState([device]));
    using var thumbnails = new EncoderThumbnailService(store, NullLogger<EncoderThumbnailService>.Instance);
    var type = typeof(EncoderThumbnailService);
    var keyType = type.GetNestedType("PreviewKey", BindingFlags.NonPublic)!;
    var key = keyType.GetMethod("From")!.Invoke(null, [device]);
    var entryType = type.GetNestedType("CachedPreview", BindingFlags.NonPublic)!;
    var unavailable = new EncoderThumbnail([], false, DateTimeOffset.UtcNow.AddSeconds(-6));
    var entry = Activator.CreateInstance(entryType, key, unavailable, 2);
    var cache = type.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(thumbnails)!;
    cache.GetType().GetProperty("Item")!.SetValue(cache, entry, [device.Id]);
    Check(ReferenceEquals(unavailable, await thumbnails.GetAsync(device.Id, CancellationToken.None)), "Repeated failure retried before backoff expired.");
    await store.UpdateAsync(state => state with { Devices = [device with { NdiChannelName = "Changed" }] });
    Check((await thumbnails.GetAsync(device.Id, CancellationToken.None)).Live, "Changed source did not bypass failure backoff.");
}

async Task Gateway()
{
    await using var deviceHost = Host("http://127.0.0.1:0");
    deviceHost.MapPost("/api/user/authorize.json", () => Results.Json(new { data = new { token = "test-token", alias = "Admin" } }));
    deviceHost.MapGet("/dashboard", () => Results.Content("<html><head></head><body>Test device UI</body></html>", "text/html"));
    await deviceHost.StartAsync();
    var device = Device("gateway", "127.0.0.1", DeviceFamily.N6) with { WebPort = new Uri(deviceHost.Urls.Single()).Port };
    using var gateway = new DeviceUiGatewayService(NullLogger<DeviceUiGatewayService>.Instance);
    foreach (var listener in new[] { "127.0.0.1", "0.0.0.0" })
    {
        await using var host = Host($"http://{listener}:0");
        var browserDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.MapPost("/__test/stop", () => { browserDone.TrySetResult(); return Results.Ok(); });
        host.Use(async (context, next) => { if (gateway.TryGetSession(context, out var session) && session is not null) await gateway.ProxyAsync(context, session, context.RequestAborted); else await next(); });
        host.MapGet("/", () => "Configurator");
        await host.StartAsync();
        var url = await gateway.CreateAsync(device, new Uri(host.Urls.Single()).Port, CancellationToken.None);
        var second = await gateway.CreateAsync(device, new Uri(host.Urls.Single()).Port, CancellationToken.None);
        Check(new Uri(url).Host.EndsWith(".localhost") && new Uri(url).Host != new Uri(second).Host, "Gateway origins are not isolated localhost names.");
        using var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        });
        var html = await client.GetStringAsync(url);
        Check(html.Contains("Test device UI") && html.Contains("sessionStorage.setItem('loginInfo'"), "Gateway did not proxy and authenticate the UI.");
        var remote = new DefaultHttpContext();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.50");
        remote.Request.Host = new HostString(new Uri(url).Host);
        Check(!gateway.TryGetSession(remote, out _), "LAN client accessed a gateway session.");
        if (args.Contains("--browser") && listener == "127.0.0.1")
        {
            Console.WriteLine($"BROWSER_GATEWAY={url}");
            Console.WriteLine($"BROWSER_STOP=http://127.0.0.1:{new Uri(host.Urls.Single()).Port}/__test/stop");
            await browserDone.Task.WaitAsync(TimeSpan.FromMinutes(3));
        }
        await host.StopAsync();
    }
    await deviceHost.StopAsync();
}

WebApplication Host(string url)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls(url);
    return builder.Build();
}

OnboardingService Onboarding(NdiTitleCardService cards) => new(store, null!, null!, null!, cards, null!, null!, NullLogger<OnboardingService>.Instance);
LastJob Job() => new("ReviewJob2", "192.0.2.20", "192.0.2.23", "192.0.2.1", now);
OnboardingRequest Settings(IReadOnlyList<string> ids) => new("192.0.2.1", "", "", "", "192.0.2.20", "192.0.2.21", "255.255.255.0", "192.0.2.1", "ReviewJob2", "192.0.2.1", ids);
ManagedDevice Device(string id, string ip, DeviceFamily family = DeviceFamily.Simulated) => new() { Id = id, IpAddress = ip, Hostname = id, MacAddress = "00:11:22:33:44:55", Family = family, Model = "N6", Role = DeviceRole.Encoder };
MulticastAssignment Assignment(ManagedDevice device) => new(device.Id, device.Hostname, device.IpAddress, device.Family.ToString(), DeviceRole.Encoder, true, false, "239.192.1.0", "255.255.255.0", 1, "applied");
MulticastConfiguration Multicast(IReadOnlyList<MulticastAssignment> assignments, string status) => new(Guid.NewGuid(), "ReviewJob2", "239.192.0.0", "255.255.0.0", "239.192.255.255", "255.255.255.0", 1, false, false, assignments, status, now);
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
static async Task InvokeTask(object target, string method, params object[] args)
{
    try { await (Task)target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!; }
    catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
}
static AppState Apply(AppState state, (ManagedDevice Original, ManagedDevice Updated)[] devices,
    (RemoteWindowsPcEndpoint Original, RemoteWindowsPcEndpoint Updated)[] remotes)
{
    var type = typeof(DeviceMonitor);
    var deviceType = type.GetNestedType("DevicePollResult", BindingFlags.NonPublic)!;
    var remoteType = type.GetNestedType("RemoteWindowsPollResult", BindingFlags.NonPublic)!;
    var deviceArray = Array.CreateInstance(deviceType, devices.Length);
    for (var i = 0; i < devices.Length; i++) deviceArray.SetValue(Activator.CreateInstance(deviceType, devices[i].Original, devices[i].Updated), i);
    var remoteArray = Array.CreateInstance(remoteType, remotes.Length);
    for (var i = 0; i < remotes.Length; i++) remoteArray.SetValue(Activator.CreateInstance(remoteType, remotes[i].Original, remotes[i].Updated, null), i);
    return (AppState)type.GetMethod("ApplyResults", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [state, deviceArray, remoteArray, null, null])!;
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Regression";
    public string EnvironmentName { get; set; } = "Regression";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
