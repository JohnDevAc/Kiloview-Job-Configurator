using System.IO.Compression;
using System.Runtime.InteropServices;

namespace NDIJobConfigurator.Core;

public sealed class DiagnosticsService(IWebHostEnvironment environment)
{
    private readonly string _logDirectory = AppDataPaths.ResolveLogDirectory(environment.ContentRootPath);

    public byte[] CreateArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var summary = archive.CreateEntry("system.txt", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(summary.Open()))
            {
                writer.WriteLine($"Product: NDI Job Configurator");
                writer.WriteLine($"Version: {BuildIdentity.Version}");
                writer.WriteLine($"Channel: {BuildIdentity.ReleaseChannel}");
                writer.WriteLine($"Generated: {DateTimeOffset.Now:O}");
                writer.WriteLine($"Operating system: {RuntimeInformation.OSDescription}");
                writer.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
                writer.WriteLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
                writer.WriteLine();
                writer.WriteLine("Application state and stored credentials are intentionally excluded.");
            }

            if (Directory.Exists(_logDirectory))
            {
                foreach (var file in new DirectoryInfo(_logDirectory)
                             .EnumerateFiles("kiloview-*.log")
                             .OrderBy(file => file.Name))
                {
                    var entry = archive.CreateEntry($"logs/{file.Name}", CompressionLevel.Optimal);
                    using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var destination = entry.Open();
                    source.CopyTo(destination);
                }
            }
        }
        return output.ToArray();
    }
}
