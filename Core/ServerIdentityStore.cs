using System.Text.Json;

namespace NDIJobConfigurator.Core;

internal static class ServerIdentityStore
{
    internal static Guid LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, "server-id.txt");
        using var gate = Acquire(Path.Combine(directory, "server-id.lock"));
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path), out var existing) && existing != Guid.Empty)
            return existing;
        Guid identity = Guid.Empty;
        foreach (var stateFile in new[] { "state.json", "state.json.bak" })
        {
            try
            {
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, stateFile)));
                if (state.RootElement.TryGetProperty("serverId", out var value)
                    && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out identity) && identity != Guid.Empty)
                    break;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        if (identity == Guid.Empty) identity = Guid.NewGuid();
        if (File.Exists(path)) File.Move(path, path + ".invalid-" + Guid.NewGuid().ToString("N"));
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(identity.ToString("D"));
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return identity;
    }

    private static FileStream Acquire(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }
}
