using KiloviewSetup.Core;

namespace KiloviewSetup.Devices;

internal sealed class TeleToolDeviceApi(ManagedDevice device, TeleToolFleetService fleet) : IDeviceApi
{
    public Task<ManagedDevice> ReadAsync(CancellationToken ct) => fleet.ReadAsync(device, device.IsOnboarded, ct);

    public Task ProvisionAccessAsync(DeviceCredentials targetCredentials, CancellationToken ct) => Task.CompletedTask;

    public Task SetNetworkAsync(string address, string mask, string gateway, CancellationToken ct) =>
        fleet.SetNetworkAsync(device, address, mask, gateway, ct);

    public Task ConfigureOnboardingAsync(OnboardingRequest settings, string hostname, string channelName, CancellationToken ct) =>
        fleet.ConfigureAsync(device, hostname, channelName, settings.JobName, settings.NdiDiscoveryServerIp, ct);

    public Task SetRoleAsync(DeviceRole role, CancellationToken ct)
    {
        if (role != DeviceRole.Encoder) throw new ArgumentException("TeleTool units are always treated as encoders.");
        return Task.CompletedTask;
    }

    public Task<HdmiProbeResult> ProbeHdmiAsync(CancellationToken ct) => Task.FromResult(new HdmiProbeResult(false, null));

    public Task ShowIdentityAsync(TitleCardSource source, CancellationToken ct) => Task.CompletedTask;

    public Task SetIdentityAsync(string hostname, string channelName, string group, CancellationToken ct) =>
        fleet.SetIdentityAsync(device, hostname, channelName, group, ct);

    public Task BlankAsync(CancellationToken ct) => Task.CompletedTask;
}
