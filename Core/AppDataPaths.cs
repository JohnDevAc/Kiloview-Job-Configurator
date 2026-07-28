namespace KiloviewSetup.Core;

public static class AppDataPaths
{
    public static string ResolveDataDirectory(string contentRootPath)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("KILOVIEW_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory);

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            localApplicationData = contentRootPath;
        return Path.Combine(localApplicationData, "Kiloview Setup");
    }

    public static string ResolveLogDirectory(string contentRootPath) =>
        Path.Combine(ResolveDataDirectory(contentRootPath), "logs");
}
