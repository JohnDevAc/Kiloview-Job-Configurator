using System.Text.Json;
using System.Text.Json.Serialization;

namespace KiloviewSetup.Core;

public sealed class AppStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppStateStore(IWebHostEnvironment environment)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("KILOVIEW_DATA_DIR");
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = environment.ContentRootPath;
        var directory = string.IsNullOrWhiteSpace(overrideDirectory) ? Path.Combine(root, "Kiloview Setup") : overrideDirectory;
        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, "state.json");
    }

    public async Task<AppState> ReadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(_file)) return AppState.Empty;
            await using var stream = File.OpenRead(_file);
            return await JsonSerializer.DeserializeAsync<AppState>(stream, _json) ?? AppState.Empty;
        }
        catch (JsonException) { return AppState.Empty; }
        finally { _gate.Release(); }
    }

    public async Task<AppState> UpdateAsync(Func<AppState, AppState> update)
    {
        await _gate.WaitAsync();
        try
        {
            AppState state;
            if (File.Exists(_file))
            {
                try
                {
                    await using var input = File.OpenRead(_file);
                    state = await JsonSerializer.DeserializeAsync<AppState>(input, _json) ?? AppState.Empty;
                }
                catch (JsonException) { state = AppState.Empty; }
            }
            else state = AppState.Empty;

            state = update(state);
            var temporary = _file + ".tmp";
            await using (var output = File.Create(temporary)) await JsonSerializer.SerializeAsync(output, state, _json);
            File.Move(temporary, _file, true);
            return state;
        }
        finally { _gate.Release(); }
    }
}
