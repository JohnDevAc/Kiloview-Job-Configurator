using KiloviewSetup.Devices;

namespace KiloviewSetup.Core;

public sealed class DeviceMonitor(AppStateStore store, DeviceClientFactory factory, ILogger<DeviceMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Device monitor pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync();
        await Parallel.ForEachAsync(state.Devices, new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct }, async (device, token) =>
        {
            ManagedDevice updated;
            try
            {
                updated = device.Family == DeviceFamily.Simulated
                    ? device with { Health = DeviceHealth.Online, LastSeenUtc = DateTimeOffset.UtcNow, LastError = null }
                    : (await factory.Create(device).ReadAsync(token)) with
                    {
                        IsOnboarded = device.IsOnboarded,
                        NdiGroup = device.NdiGroup,
                        NdiChannelName = device.NdiChannelName,
                        HdmiDisplayConnected = device.HdmiDisplayConnected,
                        HdmiOutputResolution = device.HdmiOutputResolution,
                        LastError = null
                    };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DeviceApiException)
            {
                updated = device with { Health = DeviceHealth.Offline, LastError = ex.Message };
            }
            await store.UpdateAsync(s => s with { Devices = s.Devices.Select(d => d.Id == device.Id ? updated : d).ToArray() });
        });
    }
}
