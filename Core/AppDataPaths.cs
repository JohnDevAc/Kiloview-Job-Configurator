namespace NDIJobConfigurator.Core;

public static class AppDataPaths
{
    private static readonly object MigrationLock = new();

    public static string ResolveDataDirectory(string contentRootPath)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("NDI_JOB_CONFIGURATOR_DATA_DIR")
            ?? Environment.GetEnvironmentVariable("KILOVIEW_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory);

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            localApplicationData = contentRootPath;

        var dataDirectory = Path.Combine(localApplicationData, "NDI Job Configurator");
        var legacyDirectory = Path.Combine(localApplicationData, "Kiloview Setup");
        MigrateLegacyData(legacyDirectory, dataDirectory);
        return dataDirectory;
    }

    public static string ResolveLogDirectory(string contentRootPath) =>
        Path.Combine(ResolveDataDirectory(contentRootPath), "logs");

    private static void MigrateLegacyData(string legacyDirectory, string dataDirectory)
    {
        if (Directory.Exists(dataDirectory) || !Directory.Exists(legacyDirectory)) return;

        lock (MigrationLock)
        {
            if (Directory.Exists(dataDirectory) || !Directory.Exists(legacyDirectory)) return;
            CopyDirectory(legacyDirectory, dataDirectory);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, false);
        }
    }
}
