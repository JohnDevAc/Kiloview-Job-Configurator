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
                if (device.IsSimulation())
                {
                    updated = device with { Health = DeviceHealth.Online, LastSeenUtc = DateTimeOffset.UtcNow, LastError = null };
                }
                else
                {
                    var refreshed = await factory.Create(device).ReadAsync(token);
                    updated = refreshed with
                    {
                        IsOnboarded = device.IsOnboarded,
                        NdiGroup = device.NdiGroup,
                        NdiChannelName = device.NdiChannelName,
                        HdmiDisplayConnected = device.HdmiDisplayConnected,
                        HdmiOutputResolution = device.HdmiOutputResolution,
                        MulticastConfigured = device.IsTeleTool() ? refreshed.MulticastConfigured : device.MulticastConfigured,
                        MulticastInUse = device.IsTeleTool() ? refreshed.MulticastInUse : device.MulticastConfigured,
                        MulticastNetPrefix = refreshed.MulticastNetPrefix ?? device.MulticastNetPrefix,
                        MulticastNetmask = refreshed.MulticastNetmask ?? device.MulticastNetmask,
                        MulticastTtl = refreshed.MulticastTtl ?? device.MulticastTtl,
                        MulticastLastError = refreshed.MulticastLastError ?? device.MulticastLastError,
                        LastError = null
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DeviceApiException or InvalidOperationException or System.Text.Json.JsonException)
            {
                updated = device with { Health = DeviceHealth.Offline, MulticastInUse = false, LastError = ex.Message };
            }
            await store.UpdateAsync(s => s with { Devices = s.Devices.Select(d => d.Id == device.Id ? updated : d).ToArray() });
        });
    }
}
