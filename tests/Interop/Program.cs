using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using KiloviewPcOnboarding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NDIJobConfigurator.Core;
using NdiSuite.Onboarding;

var root = Path.Combine(Path.GetTempPath(), "ndi-outcome-contract-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("Isolated interop data: " + root);

await Run("Infrastructure targets are reserved without probes", async f => {
    foreach (var ip in new[] { f.Pool[2], f.Pool[3], f.Pool[4], f.Network.Address })
        await Throws<Exception>(async () => await f.Service.StageAsync(f.Endpoint,
            new(new("static", "fixture", ip, f.Network.PrefixLength)), "127.0.0.1"));
    foreach (var status in new[] { "applying", "registered", "recovery-required" })
    {
        await f.Store.UpdateAsync(state => state with { PcOnboardingReceipts = [new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
            state.JobId!, state.JobRevision!, f.Pool[1], new("other", "static", f.Pool[6], f.Network.PrefixLength),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddHours(-1), status)] });
        await Throws<InvalidOperationException>(async () => await f.Service.StageAsync(f.Endpoint,
            new(new("static", "fixture", f.Pool[6], f.Network.PrefixLength)), "127.0.0.1"));
    }
    await f.Store.UpdateAsync(state => state with { PcOnboardingReceipts = [] });
    var safe = await f.Service.StageAsync(f.Endpoint, new(new("static", "fixture", f.Pool[6], f.Network.PrefixLength)), "127.0.0.1");
    Check(safe.Status == "staged", "Safe static target was rejected.");
});

await Run("Approval/UAC and configuration have separate bounded windows", async f => {
    var staged = await f.StageAsync();
    f.Service.RecordAgentResponse(f.Endpoint, HttpStatusCode.Accepted, staged.AttemptId);
    Check(f.Service.Status(f.Endpoint)!.RegistrationDeadlineUtc is null, "HTTP 202 started the configuration timer.");
    f.Clock.Advance(TimeSpan.FromMinutes(4));
    await f.FetchAsync(staged);
    f.Clock.Advance(TimeSpan.FromMinutes(3.5));
    await f.RegisterAsync();
    Check((await f.Store.ReadAsync()).WindowsPcs?.Count is null or 0, "Registration prematurely committed membership.");
    await f.CompleteAsync();
    Check((await f.Store.ReadAsync()).WindowsPcs?.Single().EndpointId == f.Endpoint, "Slow valid operation failed.");
});

await Run("Lost registration acknowledgements roll back and reconcile after both processes restart", async f => {
    await f.PrepareAsync();
    var file = Path.Combine(f.Directory, "ndi.json");
    File.WriteAllText(file, "previous job");
    var snapshot = ConfigurationTransaction.CaptureFiles([file]);
    var journal = f.Outcome("applying");
    OnboardingOutcomes.Write(f.AgentStatePath, journal);
    f.DropRegistrations = -1;
    await Throws<InvalidOperationException>(async () => await ConfigurationTransaction.RunAsync(async () => {
        File.WriteAllText(file, "new job");
        await f.RegisterAsync();
        return true;
    }, token => {
        ConfigurationTransaction.RestoreFiles(snapshot);
        OnboardingOutcomes.Write(f.AgentStatePath, f.Outcome("aborted"));
        return Task.CompletedTask;
    }));
    Check(f.RegistrationRequests == 3 && File.ReadAllText(file) == "previous job", "Client retry/rollback did not execute.");
    Check((await f.Store.ReadAsync()).WindowsPcs?.Count is null or 0, "Server marked a rolled-back attempt onboarded.");
    f.RestartServer();
    var saved = OnboardingOutcomes.ReadAll(f.AgentStatePath).Single();
    Check(saved.Outcome == "aborted", "PC restart lost the durable rollback outcome.");
    var response = await OnboardingOutcomes.SendAsync(f.Client, saved, CancellationToken.None);
    Check(response == "aborted", "Server restart lost the receipt needed for compensation.");
    OnboardingOutcomes.Acknowledge(f.AgentStatePath, saved);
    Check(!OnboardingOutcomes.ReadAll(f.AgentStatePath).Any(), "Acknowledged compensation remained queued.");
});

await Run("A lost final acknowledgement never rolls back applied settings", async f => {
    await f.PrepareAsync();
    f.DropRegistrations = 1;
    await f.RegisterAsync();
    Check(f.RegistrationRequests == 2, "Matching registration retry was not used.");
    var file = Path.Combine(f.Directory, "ndi.json");
    File.WriteAllText(file, "new job");
    var pending = f.Outcome("completed");
    OnboardingOutcomes.Write(f.AgentStatePath, pending);
    f.DropOutcomes = 1;
    await Throws<HttpRequestException>(() => OnboardingOutcomes.SendAsync(f.Client, pending, CancellationToken.None));
    Check((await f.Store.ReadAsync()).WindowsPcs?.Count == 1 && File.ReadAllText(file) == "new job", "Final acknowledgement loss changed local settings.");
    f.RestartServer();
    var saved = OnboardingOutcomes.ReadAll(f.AgentStatePath).Single();
    Check(await OnboardingOutcomes.SendAsync(f.Client, saved, CancellationToken.None) == "completed", "Final receipt retry was not idempotent.");
    OnboardingOutcomes.Acknowledge(f.AgentStatePath, saved);
});

await Run("Fetched permissions survive server restart without accepting unfetched requests", async f => {
    await Throws<InvalidOperationException>(() => f.Service.RegisterAsync(f.Registration(), f.Candidate()));
    await f.PrepareAsync();
    f.RestartServer();
    await f.RegisterAsync();
    await f.CompleteAsync();
});

await Run("Changed jobs and terminal outcomes reject stale completion", async f => {
    await f.PrepareAsync(); await f.RegisterAsync();
    var aborted = f.Outcome("aborted");
    Check(await OnboardingOutcomes.SendAsync(f.Client, aborted, CancellationToken.None) == "aborted", "Rollback was rejected.");
    Check(await OnboardingOutcomes.SendAsync(f.Client, f.Outcome("completed"), CancellationToken.None) == "aborted", "Late completion undid rollback.");
    Check((await f.Store.ReadAsync()).WindowsPcs?.Count is null or 0, "Aborted membership appeared.");
    await f.PrepareAsync(); await f.RegisterAsync();
    await f.Store.UpdateAsync(s => s with { LastJob = s.LastJob! with { StartedUtc = s.LastJob.StartedUtc.AddSeconds(1) } });
    Check(await OnboardingOutcomes.SendAsync(f.Client, f.Outcome("completed"), CancellationToken.None) == "superseded", "Old job completion was accepted.");
});

await Run("Expired configuration requires fresh approval while rollback receipts remain usable", async f => {
    await f.PrepareAsync();
    f.Clock.Advance(TimeSpan.FromMinutes(6));
    await Throws<InvalidOperationException>(() => f.Service.RegisterAsync(f.Registration(), f.Candidate()));
    Check(await OnboardingOutcomes.SendAsync(f.Client, f.Outcome("aborted"), CancellationToken.None) == "aborted", "Expiry prevented later rollback reconciliation.");
});

await Run("Unfinished local work reports repair and unrelated peers cannot finalize it", async f => {
    await f.PrepareAsync();
    var outcome = f.Outcome("recovery-required");
    await Throws<UnauthorizedAccessException>(() => f.Service.ReportOutcomeAsync(
        new(outcome.EndpointId, outcome.AttemptId, outcome.JobId, outcome.JobRevision, outcome.Outcome), f.Pool[7]));
    OnboardingOutcomes.Write(f.AgentStatePath, outcome);
    f.RestartServer();
    Check(await OnboardingOutcomes.SendAsync(f.Client, OnboardingOutcomes.ReadAll(f.AgentStatePath).Single(), CancellationToken.None) == "recovery-required",
        "Interrupted local work was treated as completion.");
    Check((await f.Store.ReadAsync()).WindowsPcs?.Count is null or 0, "Repair-required attempt was onboarded.");
    Check(f.Service.Status(f.Endpoint, await f.Store.ReadAsync())!.Status == "failed", "Restart concealed repair status.");
});

await Run("A denied fetch cannot revive while waiting for persisted state", async f => {
    var staged = await f.StageAsync();
    var gate = (SemaphoreSlim)typeof(AppStateStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Store)!;
    await gate.WaitAsync();
    Task fetch;
    try
    {
        fetch = f.FetchAsync(staged);
        f.Service.RecordAgentResponse(f.Endpoint, HttpStatusCode.Forbidden, staged.AttemptId);
    }
    finally { gate.Release(); }
    await Throws<InvalidOperationException>(() => fetch);
    Check(!(await f.Store.ReadAsync()).PcOnboardingReceipts?.Any() ?? true, "Denied attempt obtained a durable permission receipt.");
    Check(f.Service.Status(f.Endpoint)!.Status == "denied", "Denied attempt was revived.");
});

await Run("An offline old server and corrupt journal cannot starve later confirmations", async f => {
    await f.PrepareAsync(); await f.RegisterAsync();
    var current = f.Outcome("completed");
    var older = current with { AttemptId = Guid.NewGuid().ToString(), ServerAddress = "192.0.2.254", UpdatedUtc = current.UpdatedUtc.AddHours(-1) };
    f.OfflineServerAddress = older.ServerAddress;
    OnboardingOutcomes.Write(f.AgentStatePath, older);
    OnboardingOutcomes.Write(f.AgentStatePath, current);
    var corrupt = Path.Combine(Path.GetDirectoryName(f.AgentStatePath)!, "onboarding-outcomes", "broken.json");
    File.WriteAllText(corrupt, "{partial");
    var warnings = new List<string>();
    var available = OnboardingOutcomes.ReadAll(f.AgentStatePath, warnings.Add);
    Check(warnings.Count == 1 && File.ReadAllText(corrupt) == "{partial", "Corrupt evidence was concealed or discarded.");
    var first = OnboardingOutcomes.SelectNext(available, f.Endpoint, current.AdapterId, null)!;
    Check(first.AttemptId == older.AttemptId, "Fixture did not start at the offline job.");
    await Throws<HttpRequestException>(() => OnboardingOutcomes.SendAsync(f.Client, first, CancellationToken.None));
    var second = OnboardingOutcomes.SelectNext(available, f.Endpoint, current.AdapterId, first.AttemptId)!;
    Check(await OnboardingOutcomes.SendAsync(f.Client, second, CancellationToken.None) == "completed", "Later job was starved by an offline server.");
    OnboardingOutcomes.Acknowledge(f.AgentStatePath, second);
    Check(OnboardingOutcomes.ReadAll(f.AgentStatePath, _ => {}).Single() == older, "Retry queue discarded an unresolved outcome.");
});

await Run("A denial between fetch and persistence cannot create restart authorization", async f => {
    await f.PrepareAsync();
    var map = typeof(WindowsPcRemoteOnboardingService).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Service)!;
    var fetched = map.GetType().GetProperty("Item")!.GetValue(map, [f.Endpoint]);
    await f.Store.UpdateAsync(state => state with { PcOnboardingReceipts = [] });
    f.Service.RecordAgentResponse(f.Endpoint, HttpStatusCode.Forbidden, f.Configuration!.AttemptId);
    var persist = typeof(WindowsPcRemoteOnboardingService).GetMethod("PersistFetchedConfigurationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    await Throws<InvalidOperationException>(() => (Task)persist.Invoke(f.Service, [fetched])!);
    f.RestartServer();
    Check(!(await f.Store.ReadAsync()).PcOnboardingReceipts!.Any(), "Denied fetch persisted a permission that survived restart.");
});

Console.WriteLine("PASS: 11 cross-repository onboarding outcome scenarios.");

async Task Run(string name, Func<Fixture, Task> test)
{
    using var fixture = new Fixture(Path.Combine(root, Guid.NewGuid().ToString("N")));
    await test(fixture);
    Console.WriteLine("PASS " + name);
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

sealed class Fixture : IDisposable
{
    public string Directory { get; }
    public string AgentStatePath => Path.Combine(Directory, "pc", "agent-state.json");
    public string Endpoint { get; } = Guid.NewGuid().ToString();
    public LocalNetworkInterface Network { get; } = NetworkAddressing.GetLocalInterfaces().First(n => n.PrefixLength is >= 20 and <= 30);
    public string[] Pool { get; }
    public FakeClock Clock { get; } = new();
    public AppStateStore Store { get; private set; }
    public WindowsPcAgentService Agents { get; private set; }
    public WindowsPcRemoteOnboardingService Service { get; private set; }
    public HttpClient Client { get; }
    public WindowsPcRemoteOnboardingConfiguration? Configuration { get; private set; }
    public int DropRegistrations { get; set; }
    public int DropOutcomes { get; set; }
    public string? OfflineServerAddress { get; set; }
    public int RegistrationRequests { get; private set; }
    private readonly FixtureEnvironment _environment;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Fixture(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("NDI_JOB_CONFIGURATOR_DATA_DIR", directory);
        _environment = new() { ContentRootPath = directory };
        Pool = NetworkAddressing.ExpandCidr(NetworkAddressing.GetScanCidr(Network)).Select(ip => ip.ToString()).Where(ip => ip != Network.Address).ToArray();
        Store = new(_environment, NullLogger<AppStateStore>.Instance);
        Agents = NewAgents();
        Service = new(Store, Agents, NullLogger<WindowsPcRemoteOnboardingService>.Instance, Clock);
        Store.UpdateAsync(_ => new AppState([], new("Interop", Pool[0], Pool[^1], Pool[2], Clock.GetUtcNow()) { KiloLinkServerIp = Pool[3] },
            SelectedNetworkAdapterId: Network.Id, SelectedNetworkAddress: Network.Address)).GetAwaiter().GetResult();
        Client = new(new FixtureHandler(SendAsync));
    }
    public void RestartServer()
    {
        Store = new(_environment, NullLogger<AppStateStore>.Instance);
        Agents = NewAgents();
        Service = new(Store, Agents, NullLogger<WindowsPcRemoteOnboardingService>.Instance, Clock);
    }
    private WindowsPcAgentService NewAgents()
    {
        var agents = new WindowsPcAgentService(Store, null!, NullLogger<WindowsPcAgentService>.Instance);
        var cache = (ConcurrentDictionary<string, WindowsPcAgentSnapshot>)typeof(WindowsPcAgentService).GetField("_agents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agents)!;
        cache[Endpoint] = JsonSerializer.Deserialize<WindowsPcAgentSnapshot>(JsonSerializer.Serialize(new {
            endpointId=Endpoint,hostname="Fixture",address=Pool[0],prefixLength=Network.PrefixLength,apiPort=8094,
            adapterId="fixture",adapterName="Fixture",memberships=Array.Empty<object>(),
            capabilities=new[]{"remote-onboarding-v2","network-config-v1","onboarding-attempt-v1","onboarding-outcome-v1"},
            networkConfiguration=new{defaultGateways=new[]{Pool[4]},dnsServers=Array.Empty<string>()}
        }), Json)!;
        return agents;
    }
    public Task<WindowsPcRemoteOnboardingState> StageAsync() => Service.StageAsync(Endpoint, new(new("unchanged", "fixture")), "127.0.0.1");
    public async Task FetchAsync(WindowsPcRemoteOnboardingState staged) => Configuration = await Service.GetConfigurationAsync(Endpoint, Pool[0], staged.AttemptId);
    public async Task PrepareAsync() => await FetchAsync(await StageAsync());
    public WindowsPcRegistration Registration() => new(Endpoint, "Fixture", Pool[0], "Fixture", Network.PrefixLength, true, "6", "test", "1.0",
        AttemptId: Configuration?.AttemptId, JobId: Configuration?.JobId, JobRevision: Configuration?.JobRevision);
    public WindowsPcEndpoint Candidate() => new(Endpoint, "Fixture", Pool[0], "Fixture", Network.PrefixLength, true, "6", "test", "1.0",
        Clock.GetUtcNow(), Clock.GetUtcNow(), "onboarded", RegistrationAttemptId: Configuration?.AttemptId,
        RegistrationJobId: Configuration?.JobId, RegistrationJobRevision: Configuration?.JobRevision);
    public Task RegisterAsync() => JobConfiguratorDiscovery.RegisterAsync(
        new("fixture", "Fixture", "Fixture", Pool[0], Network.PrefixLength),
        new(Network.Address, new Uri("http://" + Network.Address + ":8091/"), "test", "test", "Interop", Pool[2], true),
        new(Endpoint, "Fixture", Pool[0], "Fixture", Network.PrefixLength, true, "6", "test", "1.0", "Windows",
            Configuration!.AttemptId, Configuration.JobId, Configuration.JobRevision), CancellationToken.None, Client);
    public PendingOutcome Outcome(string state) => new(Endpoint, Configuration!.AttemptId!, Configuration.JobId!, Configuration.JobRevision!,
        Network.Address, "fixture", state, Clock.GetUtcNow());
    public Task<string> CompleteAsync() => OnboardingOutcomes.SendAsync(Client, Outcome("completed"), CancellationToken.None);
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.Host == OfflineServerAddress) throw new HttpRequestException("Earlier server is offline");
        if (request.RequestUri!.AbsolutePath.EndsWith("/register"))
        {
            RegistrationRequests++;
            var registration = await request.Content!.ReadFromJsonAsync<WindowsPcRegistration>(Json, ct);
            await Service.RegisterAsync(registration!, Candidate());
            if (DropRegistrations != 0) { if (DropRegistrations > 0) DropRegistrations--; throw new HttpRequestException("Lost registration acknowledgement"); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "awaiting-confirmation" }) };
        }
        var outcome = await request.Content!.ReadFromJsonAsync<WindowsPcOnboardingOutcome>(Json, ct);
        var result = await Service.ReportOutcomeAsync(outcome!, Pool[0]);
        if (DropOutcomes > 0) { DropOutcomes--; throw new HttpRequestException("Lost final outcome acknowledgement"); }
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
    }
    public void Dispose() => Client.Dispose();
}
sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
}
sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now += value;
}
sealed class FixtureEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "InteropFixture";
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
