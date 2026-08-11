using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace KiloviewSetup.Core;

/// <summary>
/// Stages model-specific firmware locally and coordinates the KiloLink fleet-update step.
/// </summary>
public sealed class FirmwareService(AppStateStore store, KiloLinkCredentialStore credentials, KiloLinkServerClient kiloLink, IWebHostEnvironment environment)
{
    public const long MaximumFirmwareBytes = 1024L * 1024 * 1024;
    public const long MaximumRequestBytes = MaximumFirmwareBytes * 2 + 1024L * 1024;
    private readonly string _directory = GetFirmwareDirectory(environment);

    public async Task<FirmwareJob> StageMultipartAsync(HttpRequest request, CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var devices = state.Devices.Where(d => d.IsOnboarded && d.IsKiloview()).ToArray();
        if (devices.Length == 0) throw new InvalidOperationException("Complete initial onboarding before staging firmware.");

        var needsN6 = devices.Any(d => IsModel(d, "N6"));
        var needsN60 = devices.Any(d => IsModel(d, "N60"));
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Firmware staging requires a multipart form upload.");
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
            throw new ArgumentException("The firmware upload is missing its multipart boundary.");

        Directory.CreateDirectory(_directory);
        var packages = new List<FirmwarePackage>();
        try
        {
            var reader = new MultipartReader(boundary, request.Body)
            {
                BodyLengthLimit = MaximumFirmwareBytes
            };
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase)
                    || disposition.FileName.Value is null && disposition.FileNameStar.Value is null)
                    continue;
                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var model = fieldName switch
                {
                    "n6Firmware" => "N6",
                    "n60Firmware" => "N60",
                    _ => throw new ArgumentException($"Unexpected firmware upload field '{fieldName}'.")
                };
                if (packages.Any(package => string.Equals(package.Model, model, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Only one {model} firmware package can be staged at a time.");
                var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue
                    ? disposition.FileNameStar
                    : disposition.FileName).Value;
                packages.Add(await SaveStreamAsync(model, fileName ?? string.Empty, section.Body, ct));
            }

            if (needsN6 && packages.All(package => !string.Equals(package.Model, "N6", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Select the latest N6 firmware package.");
            if (needsN60 && packages.All(package => !string.Equals(package.Model, "N60", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Select the latest N60 firmware package.");
            var job = new FirmwareJob("staged", packages, DateTimeOffset.UtcNow,
                Message: $"{packages.Count} model-specific package(s) staged locally.");
            await store.UpdateAsync(s => s with { FirmwareJob = job });
            return job;
        }
        catch
        {
            foreach (var package in packages)
                try { File.Delete(package.LocalPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public async Task<FirmwareStartResult> StartAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync();
        var job = state.FirmwareJob ?? throw new InvalidOperationException("Stage the N6/N60 firmware packages first.");
        var lastJob = state.LastJob ?? throw new InvalidOperationException("No completed onboarding job is available.");
        var devices = state.Devices.Where(d => d.IsOnboarded && d.IsKiloview()).ToArray();
        if (devices.Length == 0) throw new InvalidOperationException("No onboarded devices are available for a fleet update.");
        ValidateCoverage(devices, job.Packages);

        if (lastJob.Simulation || devices.All(d => d.IsSimulation()))
        {
            var simulationRunning = job with { Status = "running", Message = "KiloLink simulation fleet update is running." };
            await store.UpdateAsync(s => s with { FirmwareJob = simulationRunning });
            await Task.Delay(500, ct);
            var versions = job.Packages.ToDictionary(p => p.Model, p => Path.GetFileNameWithoutExtension(p.FileName), StringComparer.OrdinalIgnoreCase);
            var completed = simulationRunning with { Status = "completed", FinishedUtc = DateTimeOffset.UtcNow, Message = "All simulated devices completed their model-specific firmware update." };
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

        var credential = credentials.ResolveAndStore(lastJob.KiloLinkServerIp, "", "");
        var dispatchPackages = job.Packages.Where(package => devices
            .Where(device => string.Equals(ModelOf(device), package.Model, StringComparison.OrdinalIgnoreCase))
            .Any(device => !PackageMatchesInstalledVersion(package, device))).ToArray();
        var currentModels = job.Packages.Except(dispatchPackages)
            .Select(package => package.Model)
            .OrderBy(model => model)
            .ToArray();
        if (dispatchPackages.Length == 0)
        {
            var message = "Every onboarded Kiloview already reports the version contained in its staged model-specific package.";
            var completed = job with { Status = "completed", FinishedUtc = DateTimeOffset.UtcNow, Message = message };
            await store.UpdateAsync(s => s with { FirmwareJob = completed });
            return new(true, true, "completed", message);
        }
        var uploadRunning = job with { Status = "uploading", Message = "Uploading model-specific packages to KiloLink Server and preparing the fleet dispatch." };
        await store.UpdateAsync(s => s with { FirmwareJob = uploadRunning });
        try
        {
            var result = await kiloLink.DispatchFleetAsync(lastJob.KiloLinkServerIp, lastJob.KiloLinkWebPort, credential, dispatchPackages, devices, ct);
            var alreadyCurrent = currentModels.Length == 0 ? "" : $" Already current and skipped: {string.Join(", ", currentModels)}.";
            var message = $"KiloLink accepted {result.PackagesUploaded} firmware package(s) and dispatched updates to {result.DevicesDispatched} device(s).{alreadyCurrent} Monitor completion in KiloLink before removing power.";
            var dispatched = uploadRunning with { Status = "dispatched", Message = message };
            await store.UpdateAsync(s => s with { FirmwareJob = dispatched });
            return new(true, false, "dispatched", message, $"http://{lastJob.KiloLinkServerIp}:{lastJob.KiloLinkWebPort}/");
        }
        catch (Exception ex)
        {
            var failed = uploadRunning with { Status = "failed", FinishedUtc = DateTimeOffset.UtcNow, Message = ex.Message };
            await store.UpdateAsync(s => s with { FirmwareJob = failed });
            throw;
        }
    }

    private async Task<FirmwarePackage> SaveStreamAsync(string model, string uploadedFileName, Stream input, CancellationToken ct)
    {
        var safeName = Path.GetFileName(uploadedFileName);
        if (!string.Equals(Path.GetExtension(safeName), ".bin", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The {model} firmware package must be a .bin file.");

        var modelDirectory = Path.Combine(_directory, model);
        Directory.CreateDirectory(modelDirectory);
        var destination = Path.Combine(modelDirectory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{safeName}");
        var temporary = destination + ".uploading";
        long length = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
            {
                var buffer = new byte[1024 * 128];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    length += read;
                    if (length > MaximumFirmwareBytes)
                        throw new ArgumentException($"The {model} firmware package exceeds the 1 GB safety limit.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct);
            }
            if (length == 0) throw new ArgumentException($"The {model} firmware package is empty.");
            File.Move(temporary, destination);
            return new(model, safeName, destination, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void ValidateCoverage(IEnumerable<ManagedDevice> devices, IReadOnlyList<FirmwarePackage> packages)
    {
        var models = packages.Select(p => p.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var model in devices.Select(ModelOf).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!models.Contains(model)) throw new InvalidOperationException($"No staged {model} firmware package covers the onboarded fleet.");
    }

    private static bool IsModel(ManagedDevice device, string model) => string.Equals(ModelOf(device), model, StringComparison.OrdinalIgnoreCase);
    private static bool PackageMatchesInstalledVersion(FirmwarePackage package, ManagedDevice device) =>
        !string.IsNullOrWhiteSpace(device.FirmwareVersion) &&
        package.FileName.Contains(device.FirmwareVersion, StringComparison.OrdinalIgnoreCase);
    private static string ModelOf(ManagedDevice device) => device.Model.StartsWith("N60", StringComparison.OrdinalIgnoreCase) ? "N60" : "N6";

    private static string GetFirmwareDirectory(IWebHostEnvironment environment)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("KILOVIEW_DATA_DIR");
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = environment.ContentRootPath;
        return Path.Combine(string.IsNullOrWhiteSpace(overrideDirectory) ? Path.Combine(root, "Kiloview Setup") : overrideDirectory, "firmware");
    }
}
