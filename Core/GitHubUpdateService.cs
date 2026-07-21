using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Serialization;

namespace KiloviewSetup.Core;

public sealed record UpdateChannelSelection(SoftwareReleaseChannel Channel);

public sealed record SystemInformation(
    string CurrentVersion,
    SoftwareReleaseChannel CurrentChannel,
    SoftwareReleaseChannel SelectedChannel,
    bool IsAdministrator,
    string RepositoryUrl);

public sealed record SoftwareUpdateInformation(
    string CurrentVersion,
    SoftwareReleaseChannel CurrentChannel,
    SoftwareReleaseChannel Channel,
    bool ChannelSwitch,
    string LatestVersion,
    bool UpdateAvailable,
    string ReleaseName,
    string ReleaseUrl,
    DateTimeOffset? PublishedAt,
    long DownloadSizeBytes,
    string Sha256);

public sealed record SoftwareUpdateLaunch(
    string Version,
    SoftwareReleaseChannel Channel,
    bool ChannelSwitch,
    string Sha256,
    bool Started);

public sealed class GitHubUpdateService(HttpClient httpClient, ILogger<GitHubUpdateService> logger)
{
    private const string RepositoryOwner = "JohnDevAc";
    private const string RepositoryName = "Kiloview-Job-Configurator";
    private const string InstallerAssetName = "Kiloview-Job-Configurator.exe";
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);

    public SystemInformation GetSystemInformation(SoftwareReleaseChannel selectedChannel) => new(
        BuildIdentity.Version,
        BuildIdentity.ReleaseChannel,
        selectedChannel,
        IsAdministrator(),
        $"https://github.com/{RepositoryOwner}/{RepositoryName}");

    public async Task<SoftwareUpdateInformation> CheckAsync(
        SoftwareReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveReleaseAsync(channel, cancellationToken);
        var currentVersion = ParseVersion(BuildIdentity.Version, "installed application version");
        var channelSwitch = channel != BuildIdentity.ReleaseChannel;
        return new(
            BuildIdentity.Version,
            BuildIdentity.ReleaseChannel,
            channel,
            channelSwitch,
            resolved.Version.Display,
            channelSwitch || resolved.Version.CompareTo(currentVersion) > 0,
            string.IsNullOrWhiteSpace(resolved.Release.Name) ? resolved.Release.TagName : resolved.Release.Name,
            resolved.Release.HtmlUrl,
            resolved.Release.PublishedAt,
            resolved.Asset.Size,
            resolved.Asset.Digest["sha256:".Length..].ToUpperInvariant());
    }

    public async Task<SoftwareUpdateLaunch> DownloadAndLaunchAsync(
        SoftwareReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Automatic updates are supported only on Windows.");
        if (!IsAdministrator())
            throw new InvalidOperationException("Kiloview Job Configurator must be running as administrator to install an update.");

        await UpdateGate.WaitAsync(cancellationToken);
        try
        {
            var resolved = await ResolveReleaseAsync(channel, cancellationToken);
            var currentVersion = ParseVersion(BuildIdentity.Version, "installed application version");
            var channelSwitch = channel != BuildIdentity.ReleaseChannel;
            if (!channelSwitch && resolved.Version.CompareTo(currentVersion) <= 0)
                throw new InvalidOperationException($"The installed {channel} version is already up to date.");

            var downloadUri = new Uri(resolved.Asset.BrowserDownloadUrl, UriKind.Absolute);
            var expectedPrefix = $"/{RepositoryOwner}/{RepositoryName}/releases/download/";
            if (!downloadUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !downloadUri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub returned an unexpected installer download URL.");

            var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localRoot))
                throw new InvalidOperationException("Local application data is unavailable.");
            var updateDirectory = Path.Combine(localRoot, "Kiloview Setup", "updates");
            Directory.CreateDirectory(updateDirectory);
            var safeVersion = string.Concat(resolved.Version.Display.Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '-'));
            var installerPath = Path.Combine(updateDirectory, $"Kiloview-Job-Configurator-{safeVersion}.exe");
            var temporaryPath = installerPath + ".download";

            try
            {
                using var download = await httpClient.GetAsync(
                    downloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength is > MaximumInstallerBytes)
                    throw new InvalidOperationException("The GitHub installer exceeds the allowed download size.");

                await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await download.Content.CopyToAsync(output, cancellationToken);

                var downloadedLength = new FileInfo(temporaryPath).Length;
                if (downloadedLength != resolved.Asset.Size)
                    throw new InvalidOperationException(
                        $"The downloaded installer size did not match GitHub ({downloadedLength} of {resolved.Asset.Size} bytes).");

                string actualSha256;
                await using (var input = File.OpenRead(temporaryPath))
                {
                    actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                }
                var expectedSha256 = resolved.Asset.Digest["sha256:".Length..].ToUpperInvariant();
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actualSha256),
                        Convert.FromHexString(expectedSha256)))
                    throw new InvalidOperationException("The downloaded installer failed SHA-256 verification.");

                File.Move(temporaryPath, installerPath, true);
                foreach (var oldInstaller in Directory.EnumerateFiles(updateDirectory, "Kiloview-Job-Configurator-*.exe"))
                    if (!oldInstaller.Equals(installerPath, StringComparison.OrdinalIgnoreCase))
                        File.Delete(oldInstaller);

                _ = Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = updateDirectory
                }) ?? throw new InvalidOperationException("Windows could not start the update installer.");

                logger.LogInformation(
                    "Verified and launched Kiloview Job Configurator {Channel} update {Version} from GitHub.",
                    channel,
                    resolved.Version.Display);
                return new(resolved.Version.Display, channel, channelSwitch, actualSha256, true);
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

    private async Task<ResolvedRelease> ResolveReleaseAsync(
        SoftwareReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        GitHubRelease release;
        if (channel == SoftwareReleaseChannel.Main)
        {
            using var response = await httpClient.GetAsync(
                $"repos/{RepositoryOwner}/{RepositoryName}/releases/latest",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken)
                ?? throw new InvalidOperationException("GitHub returned an empty Main release response.");
            if (release.Draft || release.Prerelease
                || !string.Equals(release.TargetCommitish, "main", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub's latest stable release is not targeted to the Main branch.");
        }
        else
        {
            using var response = await httpClient.GetAsync(
                $"repos/{RepositoryOwner}/{RepositoryName}/releases?per_page=30",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken)
                ?? throw new InvalidOperationException("GitHub returned an empty Development release response.");
            release = releases.FirstOrDefault(candidate =>
                    !candidate.Draft
                    && candidate.Prerelease
                    && string.Equals(candidate.TargetCommitish, "development", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("No Development prerelease has been published yet.");
        }

        var version = ParseVersion(release.TagName, $"latest {channel} release tag");
        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase)
            && candidate.State.Equals("uploaded", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Release {release.TagName} does not contain {InstallerAssetName}.");
        ValidateAsset(asset);
        return new(release, asset, version);
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static SoftwareVersion ParseVersion(string value, string field)
    {
        var display = value.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        var parts = display.Split('-', 2);
        if (!Version.TryParse(parts[0], out var core))
            throw new InvalidOperationException($"The {field} is not a valid semantic version: {value}");
        var prerelease = parts.Length == 2
            ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];
        return new(core, prerelease, display);
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

    private sealed record ResolvedRelease(GitHubRelease Release, GitHubAsset Asset, SoftwareVersion Version);

    private sealed record SoftwareVersion(Version Core, IReadOnlyList<string> Prerelease, string Display)
        : IComparable<SoftwareVersion>
    {
        public int CompareTo(SoftwareVersion? other)
        {
            if (other is null) return 1;
            var coreComparison = Core.CompareTo(other.Core);
            if (coreComparison != 0) return coreComparison;
            if (Prerelease.Count == 0) return other.Prerelease.Count == 0 ? 0 : 1;
            if (other.Prerelease.Count == 0) return -1;

            for (var index = 0; index < Math.Max(Prerelease.Count, other.Prerelease.Count); index++)
            {
                if (index >= Prerelease.Count) return -1;
                if (index >= other.Prerelease.Count) return 1;
                var comparison = CompareIdentifier(Prerelease[index], other.Prerelease[index]);
                if (comparison != 0) return comparison;
            }
            return 0;
        }

        private static int CompareIdentifier(string left, string right)
        {
            var leftNumeric = long.TryParse(left, out var leftNumber);
            var rightNumeric = long.TryParse(right, out var rightNumber);
            if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
            if (leftNumeric) return -1;
            if (rightNumeric) return 1;
            return string.Compare(left, right, StringComparison.Ordinal);
        }
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        string Name,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("target_commitish")] string TargetCommitish,
        bool Draft,
        bool Prerelease,
        IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        string Name,
        string State,
        long Size,
        string Digest,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
