using System.Security.Cryptography;

namespace KiloviewSetup.Core;

/// <summary>
/// Stages model-specific firmware locally and coordinates the KiloLink fleet-update step.
/// KiloLink Server Pro documents the user workflow, but does not publish its firmware HTTP contract;
/// a real update is therefore stopped safely until that contract is validated against the target server version.
/// </summary>
public sealed class FirmwareService(AppStateStore store, KiloLinkCredentialStore credentials, IWebHostEnvironment environment)
{
    private const long MaximumFirmwareBytes = 1024L * 1024 * 1024;
    private readonly string _directory = GetFirmwareDirectory(environment);

    public async Task<FirmwareJob> StageAsync(IFormFile? n6Firmware, IFormFile? n60Firmware, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var devices = state.Devices.Where(d => d.IsOnboarded).ToArray();
        if (devices.Length == 0) throw new InvalidOperationException("Complete initial onboarding before staging firmware.");

        var needsN6 = devices.Any(d => IsModel(d, "N6"));
        var needsN60 = devices.Any(d => IsModel(d, "N60"));
        if (needsN6 && n6Firmware is null) throw new ArgumentException("Select the latest N6 firmware package.");
        if (needsN60 && n60Firmware is null) throw new ArgumentException("Select the latest N60 firmware package.");

        Directory.CreateDirectory(_directory);
        var packages = new List<FirmwarePackage>();
        if (n6Firmware is not null) packages.Add(await SaveAsync("N6", n6Firmware, ct));
        if (n60Firmware is not null) packages.Add(await SaveAsync("N60", n60Firmware, ct));
        var job = new FirmwareJob("staged", packages, DateTimeOffset.UtcNow,
            Message: $"{packages.Count} model-specific package(s) staged locally.");
        await store.UpdateAsync(s => s with { FirmwareJob = job });
        return job;
    }

    public async Task<FirmwareStartResult> StartAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var job = state.FirmwareJob ?? throw new InvalidOperationException("Stage the N6/N60 firmware packages first.");
        var lastJob = state.LastJob ?? throw new InvalidOperationException("No completed onboarding job is available.");
        var devices = state.Devices.Where(d => d.IsOnboarded).ToArray();
        if (devices.Length == 0) throw new InvalidOperationException("No onboarded devices are available for a fleet update.");
        ValidateCoverage(devices, job.Packages);

        if (lastJob.Simulation || devices.All(d => d.Family == DeviceFamily.Simulated))
        {
            var running = job with { Status = "running", Message = "KiloLink simulation fleet update is running." };
            await store.UpdateAsync(s => s with { FirmwareJob = running });
            await Task.Delay(500, ct);
            var versions = job.Packages.ToDictionary(p => p.Model, p => Path.GetFileNameWithoutExtension(p.FileName), StringComparer.OrdinalIgnoreCase);
            var completed = running with { Status = "completed", FinishedUtc = DateTimeOffset.UtcNow, Message = "All simulated devices completed their model-specific firmware update." };
            await store.UpdateAsync(s => s with
            {
                Devices = s.Devices.Select(d => d.IsOnboarded && versions.TryGetValue(ModelOf(d), out var version) ? d with { FirmwareVersion = version } : d).ToArray(),
                FirmwareJob = completed
            });
            return new(true, true, "completed", completed.Message!);
        }

        if (string.IsNullOrWhiteSpace(lastJob.KiloLinkServerIp))
            throw new InvalidOperationException("The KiloLink server IP was not retained for this older onboarding job. Start a new onboarding run first.");
        if (!credentials.GetStatus(lastJob.KiloLinkServerIp).Stored)
            throw new InvalidOperationException("No locally stored KiloLink server credentials are available for this job.");

        var managementUrl = $"http://{lastJob.KiloLinkServerIp}";
        const string message = "Firmware is staged, but this KiloLink Server version does not expose a documented fleet-update API. Open KiloLink, upload the staged packages under Firmware Management, enable Maintenance Mode, and run the matching N6/N60 batch upgrades. The application will not guess at undocumented firmware endpoints.";
        var waiting = job with { Status = "awaiting-kilolink-api", Message = message };
        await store.UpdateAsync(s => s with { FirmwareJob = waiting });
        return new(false, false, waiting.Status, message, managementUrl);
    }

    private async Task<FirmwarePackage> SaveAsync(string model, IFormFile file, CancellationToken ct)
    {
        if (file.Length <= 0) throw new ArgumentException($"The {model} firmware package is empty.");
        if (file.Length > MaximumFirmwareBytes) throw new ArgumentException($"The {model} firmware package exceeds the 1 GB safety limit.");
        if (!string.Equals(Path.GetExtension(file.FileName), ".bin", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The {model} firmware package must be a .bin file.");

        var safeName = Path.GetFileName(file.FileName);
        var modelDirectory = Path.Combine(_directory, model);
        Directory.CreateDirectory(modelDirectory);
        var destination = Path.Combine(modelDirectory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{safeName}");
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
            await file.CopyToAsync(output, ct);
        await using var input = File.OpenRead(destination);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
        return new(model, safeName, destination, file.Length, hash);
    }

    private static void ValidateCoverage(IEnumerable<ManagedDevice> devices, IReadOnlyList<FirmwarePackage> packages)
    {
        var models = packages.Select(p => p.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var model in devices.Select(ModelOf).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!models.Contains(model)) throw new InvalidOperationException($"No staged {model} firmware package covers the onboarded fleet.");
    }

    private static bool IsModel(ManagedDevice device, string model) => string.Equals(ModelOf(device), model, StringComparison.OrdinalIgnoreCase);
    private static string ModelOf(ManagedDevice device) => device.Model.StartsWith("N60", StringComparison.OrdinalIgnoreCase) ? "N60" : "N6";

    private static string GetFirmwareDirectory(IWebHostEnvironment environment)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("KILOVIEW_DATA_DIR");
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = environment.ContentRootPath;
        return Path.Combine(string.IsNullOrWhiteSpace(overrideDirectory) ? Path.Combine(root, "Kiloview Setup") : overrideDirectory, "firmware");
    }
}
