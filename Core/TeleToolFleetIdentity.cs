namespace KiloviewSetup.Core;

public sealed record TeleToolManagerIdentity(string ManagerId, string ManagerUrl, string ManagerName);

public sealed class TeleToolFleetIdentity(AppStateStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TeleToolManagerIdentity> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync();
            var managerId = state.TeleToolManagerId;
            if (string.IsNullOrWhiteSpace(managerId))
            {
                managerId = Guid.NewGuid().ToString("N");
                await store.UpdateAsync(current => current with { TeleToolManagerId = managerId });
            }

            var host = Environment.MachineName;
            return new(
                managerId,
                $"http://{host}:8091/",
                $"{host} · Kiloview Job Configurator");
        }
        finally
        {
            _gate.Release();
        }
    }
}
