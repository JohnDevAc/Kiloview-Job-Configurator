using System.Net;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using NDIJobConfigurator.Core;
using NDIJobConfigurator.Devices;

internal static class N6IdentityRegression
{
    internal static Task SourceSelection()
    {
        var device = new ManagedDevice { Id = "source", IpAddress = "192.0.2.6", Hostname = "TUNER1", NdiChannelName = "TUNER1", MacAddress = "fixture", Model = "N6", Family = DeviceFamily.N6 };
        var apiType = typeof(DeviceClientFactory).Assembly.GetType("NDIJobConfigurator.Devices.N6DeviceApi")!;
        foreach (var methodName in new[] { "N6DecoderSourceScore", "N6PreviewScore" })
        {
            var method = apiType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
            int Score(string channel)
            {
                using var source = JsonDocument.Parse(JsonSerializer.Serialize(new { name = $"TUNER1 ({channel})", stream_name = $"TUNER1 ({channel})", address = device.IpAddress, stream_url = device.IpAddress + ":5960" }));
                return (int)method.Invoke(null, [source.RootElement, device])!;
            }
            Check(Score("TUNER1") > Score("TUNER1-HB"), "Source/preset matching treats a suffixed sibling as an exact channel.");
        }
        var runtime = typeof(EncoderThumbnailService).GetNestedType("NdiReceiveRuntime", BindingFlags.NonPublic)!;
        var find = runtime.GetMethod("FindSource", BindingFlags.NonPublic | BindingFlags.Static)!;
        var pointers = new[] { Marshal.StringToCoTaskMemUTF8("TUNER1 (TUNER1-HB)"), Marshal.StringToCoTaskMemUTF8("192.0.2.6:5961"), Marshal.StringToCoTaskMemUTF8("TUNER1 (TUNER1)"), Marshal.StringToCoTaskMemUTF8("192.0.2.6:5960") };
        var sources = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
        try
        {
            for (var i = 0; i < pointers.Length; i++) Marshal.WriteIntPtr(sources, i * IntPtr.Size, pointers[i]);
            var selected = ((string Name, string Url))find.Invoke(null, [sources, (uint)2, device])!;
            Check(selected.Name == "TUNER1 (TUNER1)", "Thumbnail selected the first suffixed sibling instead of the exact channel.");
        }
        finally
        {
            Marshal.FreeHGlobal(sources);
            foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer);
        }
        return Task.CompletedTask;
    }

    internal static async Task Onboarding(OnboardingRequest settings)
    {
        var fixture = new Clients("Channel-HX", "Channel-Full");
        await fixture.Api.ConfigureOnboardingAsync(settings, "CAM-01", "CAM-01", CancellationToken.None);
        fixture.AssertNames("CAM-01", settings.JobName);
        Check(fixture.Hostname == "CAM-01", "Onboarding lost the system hostname.");
    }

    internal static async Task Rename()
    {
        // Legacy duplicates, ordinary rename, one-sided conflict, and swapped identities.
        foreach (var names in new[] { ("CAM", "CAM"), ("Old", "Old-HB"), ("CAM-HB", "Other"), ("CAM-HB", "CAM") })
        {
            var fixture = new Clients(names.Item1, names.Item2);
            await fixture.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None);
            fixture.AssertNames("CAM", "Job");
            var writes = fixture.Writes.Count;
            await fixture.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None);
            Check(fixture.Writes.Count == writes, "An unchanged identity restarted a sender.");
            if (names == ("CAM", "CAM"))
                Check(fixture.Writes.SequenceEqual(new[] { "ndifull" }), "Repair must leave the already-correct HX sender untouched.");
        }
    }

    internal static async Task Failures()
    {
        var starting = new Clients("CAM", "CAM") { PreflightReadFailures = 2 };
        await starting.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None);
        starting.AssertNames("CAM", "Job");
        Check(starting.Writes.Count == 1, "Preflight recovery caused unnecessary naming writes.");

        var unavailable = new Clients("CAM", "CAM") { FailFullRead = true };
        await Rejected(() => unavailable.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None));
        Check(unavailable.Writes.Count == 0, "Failed profile preflight must not partially rename a device.");

        var rejected = new Clients("CAM", "CAM") { RejectFullWrite = true };
        await Rejected(() => rejected.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None));

        var lost = new Clients("CAM", "CAM") { IgnoreWrites = true };
        await Rejected(() => lost.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None));
        Check(lost.Writes.Count == 1, "Readback failure repeatedly restarted the sender.");

        var restarting = new Clients("CAM", "CAM") { RestartReadFailures = 1 };
        await restarting.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None);
        restarting.AssertNames("CAM", "Job");
        Check(restarting.Writes.Count == 1, "A transient codec restart replayed the write.");

        var waiting = new Clients("CAM", "CAM") { NotReadyStatusReads = 1 };
        await waiting.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None);
        waiting.AssertNames("CAM", "Job");
        Check(waiting.Writes.Count == 1, "A documented mode-not-ready response replayed the write.");

        using var cancellation = new CancellationTokenSource();
        var cancelled = new Clients("Old", "Old-HB") { AfterWrite = cancellation.Cancel };
        try { await cancelled.Api.SetIdentityAsync("CAM", "CAM", "Job", cancellation.Token); }
        catch (OperationCanceledException) { Check(cancelled.Writes.Count == 1, "Cancellation permitted a second write."); return; }
        throw new Exception("Cancelled identity update reported success.");
    }

    internal static async Task DecoderGuard()
    {
        var fixture = new Clients("CAM", "CAM") { Mode = "decoder" };
        await Rejected(() => fixture.Api.SetIdentityAsync("CAM", "CAM", "Job", CancellationToken.None));
        Check(fixture.Writes.Count == 0, "Decoder guard wrote an inactive encoder profile.");
    }

    internal static async Task RoleRecovery(AppStateStore store)
    {
        foreach (var (role, onboarded) in new[] { (DeviceRole.Encoder, true), (DeviceRole.Decoder, true), (DeviceRole.Encoder, false) })
        {
            var fixture = new Clients("CAM", "CAM") { Mode = role.ToString().ToLowerInvariant() };
            var device = new ManagedDevice
            {
                Id = "fixture", IpAddress = "192.0.2.6", Hostname = "Original", Model = "N6", Family = DeviceFamily.N6,
                MacAddress = "00:00:00:00:00:06", Role = DeviceRole.Decoder, IsOnboarded = onboarded,
                NdiChannelName = "CAM", NdiGroup = "Job"
            };
            await store.UpdateAsync(_ => new AppState([device]));
            var service = new OnboardingService(store, new DeviceClientFactory(store, null!, fixture), null!, null!, null!, null!, null!, NullLogger<OnboardingService>.Instance);
            var updated = await service.SetRoleAsync(device.Id, role, CancellationToken.None);
            Check(updated.Role == role && updated.Health == DeviceHealth.Online, "Role recovery did not complete.");
            if (role == DeviceRole.Encoder && onboarded) fixture.AssertNames("CAM", "Job");
            else Check(fixture.Writes.Count == 0, "Role selection edited inactive or not-yet-onboarded identities.");
        }
    }

    private static async Task Rejected(Func<Task> action)
    {
        try { await action(); }
        catch (DeviceApiException) { return; }
        throw new Exception("An unsuccessful N6 identity change was reported as successful.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Clients(string hx, string full) : IHttpClientFactory
    {
        internal readonly Dictionary<string, (string Name, string Group)> Profiles = new()
        {
            ["ndihx"] = (hx, "Job"), ["ndifull"] = (full, "Job")
        };
        internal readonly List<string> Writes = [];
        internal string Mode = "encoder";
        internal string Hostname = "Original";
        internal bool FailFullRead, RejectFullWrite, IgnoreWrites;
        internal int RestartReadFailures;
        internal int PreflightReadFailures;
        internal int NotReadyStatusReads;
        internal Action? AfterWrite;
        internal IDeviceApi Api => new DeviceClientFactory(null!, null!, this).Create(new ManagedDevice
        {
            Id = "fixture", IpAddress = "192.0.2.6", Hostname = Hostname,
            MacAddress = "00:00:00:00:00:06", Model = "N6", Family = DeviceFamily.N6, Role = DeviceRole.Encoder
        });

        internal void AssertNames(string name, string group)
        {
            Check(Profiles["ndihx"] == (name, group) && Profiles["ndifull"] == (name + "-HB", group), "Saved N6 profile identities are not distinct and correct.");
        }

        public HttpClient CreateClient(string name) => new(new Handler(this));
    }

    private sealed class Handler(Clients fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            using var body = JsonDocument.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct));
            var root = body.RootElement;
            object result;
            switch (path)
            {
                case "/api/user/authorize.json": result = new { result = "ok", data = new { token = "fixture", alias = "Fixture" } }; break;
                case "/api/mode/get.json": result = new { result = "ok", data = new { mode = fixture.Mode } }; break;
                case "/api/mode/status.json":
                    result = fixture.NotReadyStatusReads-- > 0
                        ? new { result = "error", msg = "waitChangeModeError" }
                        : (object)new { result = "ok", data = new { mode = fixture.Mode, status = "ready" } }; break;
                case "/api/firmware/get.json": result = new { result = "ok", data = new { softwareVersion = "fixture" } }; break;
                case "/api/device/get_hostname.json": result = new { result = "ok", data = new { hostname = fixture.Hostname } }; break;
                case "/api/network/get.json": result = new { result = "ok", data = new[] { new { ip = "192.0.2.6", dynamic = "n", mac = "00:00:00:00:00:06" } } }; break;
                case "/api/device/status.json": result = new { result = "ok", data = new { serial_number = "fixture" } }; break;
                case "/api/decoder/current/get.json": result = new { result = "ok", data = new { name = "Fixture (source)" } }; break;
                case "/api/KiloLink/set":
                case "/api/device/set_discovery_server.json": result = new { result = "ok" }; break;
                case "/api/device/set_hostname.json":
                    fixture.Hostname = root.GetProperty("hostname").GetString()!;
                    result = new { result = "ok" }; break;
                case "/api/encoder/ndi/get_config.json":
                {
                    var type = root.GetProperty("types").GetString()!;
                    if (type == "ndifull" && fixture.FailFullRead) result = new { result = "error", msg = "Full profile unavailable" };
                    else if (fixture.Writes.Count == 0 && fixture.PreflightReadFailures-- > 0) result = new { result = "error", msg = "0201001" };
                    else if (fixture.Writes.Count > 0 && fixture.RestartReadFailures-- > 0) result = new { result = "error", msg = "0201001" };
                    else
                    {
                        var profile = fixture.Profiles[type];
                        result = new { result = "ok", data = new { channel_name = profile.Name, device_group = profile.Group, ndi_connection = "multicast", netprefix = "239.192.1.0", netmask = "255.255.255.0", ttl = 1, codec = "H264" } };
                    }
                    break;
                }
                case "/api/device/modify.json":
                case "/api/encoder/ndi/set_config.json":
                {
                    var type = root.GetProperty("types").GetString()!;
                    var channel = root.GetProperty("channel_name").GetString()!;
                    var other = fixture.Profiles[type == "ndihx" ? "ndifull" : "ndihx"].Name;
                    fixture.Writes.Add(type);
                    Check(!string.Equals(channel, other, StringComparison.OrdinalIgnoreCase), "N6 write collides with the other profile's channel name.");
                    Check(root.EnumerateObject().All(p => p.Name is "types" or "channel_name" or "device_group" or "machine_name"), "Identity update changed codec/transport settings.");
                    if (type == "ndifull" && fixture.RejectFullWrite) result = new { result = "error", msg = "0201001" };
                    else
                    {
                        if (!fixture.IgnoreWrites) fixture.Profiles[type] = (channel, root.GetProperty("device_group").GetString()!);
                        fixture.AfterWrite?.Invoke();
                        result = new { result = "ok" };
                    }
                    break;
                }
                default: throw new Exception("Unexpected N6 request: " + path);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }
}
