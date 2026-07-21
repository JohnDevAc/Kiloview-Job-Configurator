using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

public sealed class DeviceClientFactory(AppStateStore store, TeleToolFleetService teleTools)
{
    public IDeviceApi Create(ManagedDevice device) => device.Family switch
    {
        DeviceFamily.N6 => new N6DeviceApi(device.IpAddress, device.Credentials),
        DeviceFamily.N60 => new N60DeviceApi(device.IpAddress, device.Credentials),
        DeviceFamily.TeleTool => new TeleToolDeviceApi(device, teleTools),
        DeviceFamily.Simulated => new SimulatedDeviceApi(store, device.Id),
        DeviceFamily.SimulatedTeleTool => new TeleToolDeviceApi(device, teleTools),
        _ => throw new NotSupportedException($"Unsupported device family {device.Family}.")
    };

    public async Task<ManagedDevice?> ProbeAsync(string ipAddress, DeviceCredentials credentials, CancellationToken ct)
    {
        var attempts = new List<(DeviceFamily Family, DeviceCredentials Credentials)>
        {
            (DeviceFamily.N60, credentials)
        };
        if (credentials.Username == "admin" && credentials.Password == "admin")
            attempts.Add((DeviceFamily.N60, credentials with { Password = "Admin123" }));
        attempts.Add((DeviceFamily.N6, credentials));

        foreach (var attempt in attempts)
        {
            try
            {
                IDeviceApi api = attempt.Family == DeviceFamily.N60
                    ? new N60DeviceApi(ipAddress, attempt.Credentials)
                    : new N6DeviceApi(ipAddress, attempt.Credentials);
                return await api.ReadAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DeviceApiException or InvalidOperationException or KeyNotFoundException)
            {
                // A probe is expected to fail for non-Kiloview hosts and the other API family.
            }
        }
        return null;
    }

    public Task<ManagedDevice?> ProbeTeleToolAsync(string ipAddress, CancellationToken ct) =>
        teleTools.ProbeAsync(ipAddress, TeleToolFleetService.DefaultPort, ct);
}

internal sealed class SimulatedDeviceApi(AppStateStore store, string id) : IDeviceApi
{
    private async Task<ManagedDevice> Device() => (await store.ReadAsync()).Devices.FirstOrDefault(d => d.Id == id)
        ?? throw new KeyNotFoundException($"Simulated device {id} no longer exists.");

    private async Task Change(Func<ManagedDevice, ManagedDevice> update) => await store.UpdateAsync(s =>
        s with { Devices = s.Devices.Select(d => d.Id == id ? update(d) : d).ToArray() });

    public Task<ManagedDevice> ReadAsync(CancellationToken ct) => Device();
    public async Task ProvisionAccessAsync(DeviceCredentials targetCredentials, CancellationToken ct) =>
        await Change(d => d with { Credentials = targetCredentials, LicenseAccepted = true });
    public async Task SetNetworkAsync(string address, string mask, string gateway, CancellationToken ct) =>
        await Change(d => d with { IpAddress = address, IsStatic = true, LastSeenUtc = DateTimeOffset.UtcNow });
    public async Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct) =>
        await Change(d => d with { Hostname = hostname, NdiChannelName = channelName, NdiGroup = settings.JobName, IsOnboarded = true });
    public async Task SetRoleAsync(DeviceRole role, CancellationToken ct) => await Change(d => d with { Role = role });
    public async Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct)
    {
        var device = await Device();
        return new(device.HdmiDisplayConnected == true, device.HdmiDisplayConnected == true ? device.HdmiOutputResolution ?? "1920x1080p60" : null);
    }
    public Task ShowIdentityAsync(TitleCardSource source, CancellationToken ct) => Task.CompletedTask;
    public async Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct) =>
        await Change(d => d with { Hostname = hostname, NdiChannelName = channelName, NdiGroup = group });
    public Task BlankAsync(CancellationToken ct) => Task.CompletedTask;
}
