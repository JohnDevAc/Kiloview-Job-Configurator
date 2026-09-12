using System.Text.Json;
using System.Text.Json.Serialization;

namespace NDIJobConfigurator.Core;

public sealed class AppStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private readonly string _backup;
    private readonly Guid _serverId;
    private readonly ILogger<AppStateStore> _logger;
    private AppState? _cached;
    private (DateTime LastWriteUtc, long Length)? _cachedStamp;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppStateStore(IWebHostEnvironment environment, ILogger<AppStateStore> logger)
    {
        _logger = logger;
        var directory = AppDataPaths.ResolveDataDirectory(environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        _serverId = ServerIdentityStore.LoadOrCreate(directory);
        _file = Path.Combine(directory, "state.json");
        _backup = Path.Combine(directory, "state.json.bak");
    }

    public async Task<AppState> ReadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await ReadStateUnsafeAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task<AppState> UpdateAsync(Func<AppState, AppState> update)
    {
        await _gate.WaitAsync();
        try
        {
            var state = await ReadStateUnsafeAsync();
            var updated = update(state);
            if (ReferenceEquals(updated, state)) return state;
            ValidateShape(updated);
            updated = Freeze(updated);

            await PersistStateUnsafeAsync(updated);
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task<AppState> ReadStateUnsafeAsync()
    {
        var stamp = StateStamp();
        if (_cached is not null && stamp == _cachedStamp) return _cached;
        var loaded = await LoadStateUnsafeAsync();
        var state = Freeze(loaded);
        // Publish the same identity into the credential-bearing snapshot before
        // returning it over HTTP, including immediately after a legacy upgrade.
        if (loaded.ServerId != _serverId) await PersistStateUnsafeAsync(state);
        _cached = state;
        _cachedStamp = StateStamp();
        return state;
    }

    private async Task PersistStateUnsafeAsync(AppState state)
    {
        var temporary = _file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, state, _json);
                await output.FlushAsync();
                output.Flush(true);
            }
            ReplaceStateFile(temporary);
            _cached = state;
            _cachedStamp = StateStamp();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Force a reload after an external edit that retained file metadata.</summary>
    public async Task<AppState> ReloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _cached = null;
            return await ReadStateUnsafeAsync();
        }
        finally { _gate.Release(); }
    }

    private (DateTime LastWriteUtc, long Length)? StateStamp()
    {
        var file = new FileInfo(_file);
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : null;
    }

    // Records protect scalar values; copy and wrap their collections as well so
    // callers cannot mutate a cached snapshot without an atomic UpdateAsync.
    private AppState Freeze(AppState state) => state with
    {
        ServerId = _serverId,
        PcOnboardingReceipts = state.PcOnboardingReceipts is null ? null : Array.AsReadOnly(
            state.PcOnboardingReceipts.Select(receipt => receipt with {
                Network = receipt.Network with { DnsServers = receipt.Network.DnsServers is null ? null : Array.AsReadOnly(receipt.Network.DnsServers.ToArray()) },
                Candidate = receipt.Candidate is null ? null : receipt.Candidate with {
                    AgentCapabilities = receipt.Candidate.AgentCapabilities is null ? null : Array.AsReadOnly(receipt.Candidate.AgentCapabilities.ToArray())
                }
            }).ToArray()),
        Devices = Array.AsReadOnly(state.Devices.ToArray()),
        FirmwareJob = state.FirmwareJob is null ? null : state.FirmwareJob with
        {
            Packages = Array.AsReadOnly(state.FirmwareJob.Packages.ToArray())
        },
        Multicast = state.Multicast is null ? null : state.Multicast with
        {
            Assignments = Array.AsReadOnly(state.Multicast.Assignments.ToArray())
        },
        WindowsPcs = state.WindowsPcs is null ? null : Array.AsReadOnly(
            state.WindowsPcs.Select(endpoint => endpoint with
            {
                AgentCapabilities = endpoint.AgentCapabilities is null
                    ? null : Array.AsReadOnly(endpoint.AgentCapabilities.ToArray())
            }).ToArray())
    };

    private async Task<AppState> LoadStateUnsafeAsync()
    {
        if (!File.Exists(_file))
        {
            if (File.Exists(_backup))
            {
                var recovered = await TryReadAsync(_backup);
                if (recovered is not null)
                {
                    File.Copy(_backup, _file, overwrite: true);
                    _logger.LogWarning("Restored missing application state from {BackupFile}", _backup);
                    return recovered;
                }
                var quarantinedBackup = Quarantine(_backup);
                throw new InvalidDataException(
                    $"The application state backup was corrupt and has been preserved at '{quarantinedBackup}'.");
            }
            if (Directory.EnumerateFiles(Path.GetDirectoryName(_file)!, "state.corrupt-*.json").Any())
                throw new InvalidDataException("Application state requires repair. Restore a valid state file or backup; quarantined state has been preserved.");
            return AppState.Empty;
        }

        var state = await TryReadAsync(_file);
        if (state is not null) return state;

        var quarantined = Quarantine(_file);
        _logger.LogError("Application state was invalid and has been quarantined at {QuarantinedFile}", quarantined);
        var backup = await TryReadAsync(_backup);
        if (backup is not null)
        {
            File.Copy(_backup, _file, overwrite: true);
            _logger.LogWarning("Recovered application state from {BackupFile}", _backup);
            return backup;
        }

        throw new InvalidDataException(
            $"Application state was corrupt and no valid backup was available. The invalid file was preserved at '{quarantined}'.");
    }

    private async Task<AppState?> TryReadAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = await JsonSerializer.DeserializeAsync<AppState>(stream, _json);
            ValidateShape(state);
            return state;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not deserialize application state file {StateFile}", path);
            return null;
        }
    }

    private static void ValidateShape(AppState? state)
    {
        static void Collection<T>(IReadOnlyList<T>? values, string name) where T : class
        {
            if (values is null || values.Any(value => value is null))
                throw new JsonException($"Application state requires a non-null {name} collection with non-null entries.");
        }
        static void Endpoint(WindowsPcEndpoint endpoint)
        {
            if (endpoint.AgentCapabilities is not null) Collection(endpoint.AgentCapabilities, "agent capabilities");
        }
        if (state is null) throw new JsonException("Application state must be an object.");
        Collection(state.Devices, "devices");
        if (state.Devices.Any(device => device.Credentials is null))
            throw new JsonException("A device's credentials object cannot be null.");
        if (state.FirmwareJob is not null) Collection(state.FirmwareJob.Packages, "firmware packages");
        if (state.Multicast is not null) Collection(state.Multicast.Assignments, "multicast assignments");
        if (state.WindowsPcs is not null)
        {
            Collection(state.WindowsPcs, "Windows PCs");
            foreach (var endpoint in state.WindowsPcs) Endpoint(endpoint);
        }
        if (state.PcOnboardingReceipts is not null)
        {
            Collection(state.PcOnboardingReceipts, "onboarding receipts");
            foreach (var receipt in state.PcOnboardingReceipts)
            {
                if (receipt.Network is null) throw new JsonException("An onboarding receipt requires its network configuration.");
                if (receipt.Network.DnsServers is not null) Collection(receipt.Network.DnsServers, "DNS servers");
                if (receipt.Candidate is not null) Endpoint(receipt.Candidate);
            }
        }
    }

    private string Quarantine(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var quarantine = Path.Combine(directory, $"state.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        File.Move(path, quarantine);
        return quarantine;
    }

    private void ReplaceStateFile(string temporary)
    {
        if (!File.Exists(_file))
        {
            File.Move(temporary, _file);
            RefreshBackup();
            return;
        }

        try
        {
            File.Replace(temporary, _file, _backup, ignoreMetadataErrors: true);
            RefreshBackup();
        }
        catch (PlatformNotSupportedException)
        {
            RefreshBackup();
            File.Move(temporary, _file, overwrite: true);
            RefreshBackup();
        }
    }

    private void RefreshBackup()
    {
        try
        {
            File.Copy(_file, _backup, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Application state was saved, but its backup could not be refreshed");
        }
    }
}
