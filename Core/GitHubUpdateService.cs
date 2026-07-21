using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Serialization;

namespace KiloviewSetup.Core;

public sealed record SystemInformation(string CurrentVersion, bool IsAdministrator, string RepositoryUrl);

public sealed record SoftwareUpdateInformation(
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string ReleaseName,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    long DownloadSizeBytes,
    string Sha256);

public sealed record SoftwareUpdateLaunch(string Version, string Sha256, bool Started);

public sealed class GitHubUpdateService(HttpClient httpClient, ILogger<GitHubUpdateService> logger)
{
    private const string RepositoryOwner = "JohnDevAc";
    private const string RepositoryName = "Kiloview-Job-Configurator";
    private const string InstallerAssetName = "Kiloview-Job-Configurator.exe";
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);

    public SystemInformation GetSystemInformation() => new(
        CurrentVersion,
        IsAdministrator(),
        $"https://github.com/{RepositoryOwner}/{RepositoryName}");

    public async Task<SoftwareUpdateInformation> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"repos/{RepositoryOwner}/{RepositoryName}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty release response.");
        var latestVersion = ParseVersion(release.TagName, "latest release tag");
        var currentVersion = ParseVersion(CurrentVersion, "installed application version");
        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase)
            && candidate.State.Equals("uploaded", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Release {release.TagName} does not contain {InstallerAssetName}.");

        ValidateAsset(asset);
        return new(
            CurrentVersion,
            latestVersion.ToString(3),
            latestVersion > currentVersion,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            release.HtmlUrl,
            release.PublishedAt,
            asset.Size,
            asset.Digest["sha256:".Length..].ToUpperInvariant());
    }

    public async Task<SoftwareUpdateLaunch> DownloadAndLaunchAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Automatic updates are supported only on Windows.");
        if (!IsAdministrator())
            throw new InvalidOperationException("Kiloview Job Configurator must be running as administrator to install an update.");

        await UpdateGate.WaitAsync(cancellationToken);
        try
        {
            using var releaseResponse = await httpClient.GetAsync(
                $"repos/{RepositoryOwner}/{RepositoryName}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            releaseResponse.EnsureSuccessStatusCode();
            var release = await releaseResponse.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken)
                ?? throw new InvalidOperationException("GitHub returned an empty release response.");
            var latestVersion = ParseVersion(release.TagName, "latest release tag");
            var currentVersion = ParseVersion(CurrentVersion, "installed application version");
            if (latestVersion <= currentVersion)
                throw new InvalidOperationException("The installed version is already up to date.");

            var asset = release.Assets.FirstOrDefault(candidate =>
                candidate.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase)
                && candidate.State.Equals("uploaded", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Release {release.TagName} does not contain {InstallerAssetName}.");
            ValidateAsset(asset);

            var downloadUri = new Uri(asset.BrowserDownloadUrl, UriKind.Absolute);
            var expectedPrefix = $"/{RepositoryOwner}/{RepositoryName}/releases/download/";
            if (!downloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !downloadUri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub returned an unexpected installer download URL.");

            var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localRoot)) throw new InvalidOperationException("Local application data is unavailable.");
            var updateDirectory = Path.Combine(localRoot, "Kiloview Setup", "updates");
            Directory.CreateDirectory(updateDirectory);
            var installerPath = Path.Combine(updateDirectory, $"Kiloview-Job-Configurator-{latestVersion.ToString(3)}.exe");
            var temporaryPath = installerPath + ".download";

            try
            {
                using var download = await httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength is > MaximumInstallerBytes)
                    throw new InvalidOperationException("The GitHub installer exceeds the allowed download size.");

                await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await download.Content.CopyToAsync(output, cancellationToken);

                var downloadedLength = new FileInfo(temporaryPath).Length;
                if (downloadedLength != asset.Size)
                    throw new InvalidOperationException($"The downloaded installer size did not match GitHub ({downloadedLength} of {asset.Size} bytes).");

                string actualSha256;
                await using (var input = File.OpenRead(temporaryPath))
                {
                    actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                }
                var expectedSha256 = asset.Digest["sha256:".Length..].ToUpperInvariant();
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualSha256), Convert.FromHexString(expectedSha256)))
                    throw new InvalidOperationException("The downloaded installer failed SHA-256 verification.");

                File.Move(temporaryPath, installerPath, true);
                foreach (var oldInstaller in Directory.EnumerateFiles(updateDirectory, "Kiloview-Job-Configurator-*.exe"))
                    if (!oldInstaller.Equals(installerPath, StringComparison.OrdinalIgnoreCase)) File.Delete(oldInstaller);

                _ = Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = updateDirectory
                }) ?? throw new InvalidOperationException("Windows could not start the update installer.");

                logger.LogInformation("Verified and launched Kiloview Job Configurator update {Version} from GitHub.", latestVersion.ToString(3));
                return new(latestVersion.ToString(3), actualSha256, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            UpdateGate.Release();
        }
    }

    private static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static Version ParseVersion(string value, string field)
    {
        var normalized = value.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        if (!Version.TryParse(normalized, out var version))
            throw new InvalidOperationException($"The {field} is not a valid version: {value}");
        return version;
    }

    private static void ValidateAsset(GitHubAsset asset)
    {
        if (asset.Size is <= 0 or > MaximumInstallerBytes)
            throw new InvalidOperationException("The GitHub installer has an invalid size.");
        if (string.IsNullOrWhiteSpace(asset.Digest)
            || !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || asset.Digest.Length != "sha256:".Length + 64
            || !asset.Digest["sha256:".Length..].All(Uri.IsHexDigit))
            throw new InvalidOperationException("The GitHub release does not provide a valid SHA-256 digest for the installer.");
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        string Name,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        string Name,
        string State,
        long Size,
        string Digest,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
