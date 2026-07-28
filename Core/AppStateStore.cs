using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiloviewSetup.Core;

public sealed class AppStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private readonly string _backup;
    private readonly ILogger<AppStateStore> _logger;
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
