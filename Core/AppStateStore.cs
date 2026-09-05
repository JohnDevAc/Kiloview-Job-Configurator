using System.Text.Json;
using System.Text.Json.Serialization;

namespace NDIJobConfigurator.Core;

public sealed class AppStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private readonly string _backup;
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
            updated = Freeze(updated);

            var temporary = _file + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(output, updated, _json);
                    await output.FlushAsync();
                    output.Flush(flushToDisk: true);
                }
                ReplaceStateFile(temporary);
                _cached = updated;
                _cachedStamp = StateStamp();
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task<AppState> ReadStateUnsafeAsync()
    {
        var stamp = StateStamp();
        if (_cached is not null && stamp == _cachedStamp) return _cached;
        var state = Freeze(await LoadStateUnsafeAsync());
        _cached = state;
        _cachedStamp = StateStamp();
        return state;
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
    private static AppState Freeze(AppState state) => state with
    {
        Devices = Array.AsReadOnly(state.Devices.ToArray()),
        FirmwareJob = state.FirmwareJob is null ? null : state.FirmwareJob with
        {
            Packages = Array.AsReadOnly(state.FirmwareJob.Packages.ToArray())
        },
        Multicast = state.Multicast is null ? null : state.Multicast with
        {
            Assignments = Array.AsReadOnly(state.Multicast.Assignments.ToArray())
        },
        RemoteWindowsPcs = state.RemoteWindowsPcs is null ? null : Array.AsReadOnly(
            state.RemoteWindowsPcs.Select(endpoint => endpoint with
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
            return await JsonSerializer.DeserializeAsync<AppState>(stream, _json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not deserialize application state file {StateFile}", path);
            return null;
        }
    }

    private string Quarantine(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var quarantine = Path.Combine(directory, $"state.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json");
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
