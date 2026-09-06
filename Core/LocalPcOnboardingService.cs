using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace NDIJobConfigurator.Core;

public sealed record LocalPcComponentStatus(bool Installed, bool Compatible, string? Version, string? Error)
{
    public string Hostname => Environment.MachineName;
    public string OperatingSystemVersion => RuntimeInformation.OSDescription.Trim();
}
public interface ILocalPcOnboarding
{
    LocalPcComponentStatus Status { get; }
    Task<WindowsPcEndpoint> OnboardAsync(LocalNetworkInterface network, string jobName, string discoveryServerIp, CancellationToken ct);
}

public sealed class LocalPcOnboardingService : ILocalPcOnboarding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string UtilityPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "NDI Configurator", "PC Agent", "NDI Configurator PC Agent Setup.exe");

    public LocalPcComponentStatus Status
    {
        get
        {
            if (!File.Exists(UtilityPath)) return new(false, false, null, "Install the PC Agent component to onboard this server PC.");
            var version = FileVersionInfo.GetVersionInfo(UtilityPath).ProductVersion;
            var agentPath = Path.Combine(Path.GetDirectoryName(UtilityPath)!, "NDI Configurator PC Agent.exe");
            var agentVersion = File.Exists(agentPath) ? FileVersionInfo.GetVersionInfo(agentPath).ProductVersion : null;
            var compatible = IsCompatibleVersion(version) && IsCompatibleVersion(agentVersion);
            return new(true, compatible, version, compatible ? null : "Update the PC Agent component to version 0.7.0 or later.");
        }
    }

    private static bool IsCompatibleVersion(string? version) =>
        Version.TryParse(version?.Split('-', '+')[0], out var parsed) && parsed >= new Version(0, 7, 0);

    public async Task<WindowsPcEndpoint> OnboardAsync(LocalNetworkInterface network, string jobName, string discoveryServerIp, CancellationToken ct)
    {
        var status = Status;
        if (!status.Compatible) throw new InvalidOperationException(status.Error);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run the installed Job Configurator as administrator to onboard this PC.");
        var start = new ProcessStartInfo(UtilityPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(UtilityPath)!
        };
        start.ArgumentList.Add("--server-command");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PC Agent Setup could not be started.");
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                operation = "onboard",
                adapterId = network.Id,
                address = network.Address,
                jobName,
                ndiDiscoveryServerIp = discoveryServerIp
            }, Json).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var response = JsonSerializer.Deserialize<CommandResponse>(await output, Json)
                ?? throw new InvalidOperationException("PC Agent Setup returned no result.");
            _ = await error;
            if (process.ExitCode != 0 || response.SchemaVersion != 1 || !response.Success)
                throw new InvalidOperationException(response.Error ?? "Local PC onboarding failed.");
            return ValidateResult(response.Endpoint, network);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("PC Agent Setup timed out while onboarding this server PC. Check the agent and reapply onboarding.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    internal static WindowsPcEndpoint ValidateResult(WindowsPcRegistration? endpoint, LocalNetworkInterface network)
    {
        if (endpoint is null || !Guid.TryParse(endpoint.EndpointId, out var id) || id == Guid.Empty
            || endpoint.Address != network.Address || endpoint.PrefixLength != network.PrefixLength
            || !endpoint.PreferredInterfaceConfigured || endpoint.EulaVersion != "1.0"
            || !string.Equals(endpoint.Hostname, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(endpoint.AdapterName))
            throw new InvalidOperationException("PC Agent Setup did not verify this server's selected network interface.");
        var now = DateTimeOffset.UtcNow;
        return new(endpoint.EndpointId, endpoint.Hostname, endpoint.Address, endpoint.AdapterName, endpoint.PrefixLength,
            true, endpoint.NdiToolsVersion, endpoint.UtilityVersion, endpoint.EulaVersion, now, now, "onboarded",
            OperatingSystemVersion: endpoint.OperatingSystemVersion, IsServerPc: true);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[32 * 1024 + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (count > 32 * 1024) throw new InvalidOperationException("PC Agent Setup returned an oversized response.");
        return new string(buffer, 0, count);
    }

    private sealed record CommandResponse(int SchemaVersion, bool Success, string Version, WindowsPcRegistration? Endpoint, string? Error);
}
