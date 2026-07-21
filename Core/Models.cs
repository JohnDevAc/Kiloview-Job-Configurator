using System.Net;

namespace KiloviewSetup.Core;

public enum DeviceFamily { N6, N60, Simulated }
public enum DeviceRole { Unknown, Encoder, Decoder }
public enum DeviceHealth { Unknown, Online, Offline, Configuring, Error }

public sealed record DeviceCredentials(string Username = "admin", string Password = "admin");

public sealed record ManagedDevice
{
    public required string Id { get; init; }
    public required string IpAddress { get; init; }
    public required string MacAddress { get; init; }
    public required string Hostname { get; init; }
    public required string Model { get; init; }
    public required DeviceFamily Family { get; init; }
    public DeviceRole Role { get; init; } = DeviceRole.Unknown;
    public DeviceHealth Health { get; init; } = DeviceHealth.Unknown;
    public string NdiGroup { get; init; } = "public";
    public string NdiChannelName { get; init; } = "Channel-1";
    public string? FirmwareVersion { get; init; }
    public bool IsStatic { get; init; }
    public bool IsOnboarded { get; init; }
    public bool LicenseAccepted { get; init; }
    public bool? HdmiDisplayConnected { get; init; }
    public string? HdmiOutputResolution { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
    public DeviceCredentials Credentials { get; init; } = new();
}

public sealed record LastJob(string JobName, string StaticStart, string StaticEnd, string NdiDiscoveryServerIp, DateTimeOffset StartedUtc)
{
    public string KiloLinkServerIp { get; init; } = "";
    public int KiloLinkWebPort { get; init; } = 80;
    public bool Simulation { get; init; }
}

public sealed record FirmwarePackage(string Model, string FileName, string LocalPath, long SizeBytes, string Sha256);
public sealed record FirmwareJob(string Status, IReadOnlyList<FirmwarePackage> Packages, DateTimeOffset StagedUtc, DateTimeOffset? FinishedUtc = null, string? Message = null);
public sealed record FirmwareStartResult(bool Started, bool Completed, string Status, string Message, string? ManagementUrl = null);
public sealed record KiloLinkConnectionRequest(string ServerIp, int WebPort, string Username, string Password);
public sealed record KiloLinkConnectionStatus(string Version, IReadOnlyList<string> DeviceTypes, IReadOnlyList<string> FirmwareTypes, int DeviceCount);
public sealed record KiloLinkServerDiscovery(string ServerIp, int WebPort, string Version);
public sealed record KiloLinkAuthorizationResult(string SerialNumber, string Hostname, string AuthorizationCode, bool Created);
public sealed record KiloLinkFleetResult(int PackagesUploaded, int DevicesDispatched, IReadOnlyList<string> Models);

public sealed record AppState(IReadOnlyList<ManagedDevice> Devices, LastJob? LastJob = null, FirmwareJob? FirmwareJob = null)
{
    public static AppState Empty => new([]);
}

public sealed record DiscoveryRequest(
    IReadOnlyList<string>? ScanCidrs,
    DeviceCredentials? Credentials,
    bool Simulation = false);

public sealed record DiscoveryResult(IReadOnlyList<ManagedDevice> Devices, IReadOnlyList<string> ScannedCidrs, TimeSpan Duration);

public sealed record OnboardingRequest(
    string KiloLinkServerIp,
    string KiloLinkOnboardingCode,
    string KiloLinkUsername,
    string KiloLinkPassword,
    string StaticStart,
    string StaticEnd,
    string SubnetMask,
    string Gateway,
    string JobName,
    string NdiDiscoveryServerIp,
    IReadOnlyList<string> DeviceIds,
    IReadOnlyDictionary<string, DeviceRole>? RoleOverrides = null,
    int KiloLinkPort = 50000,
    int KiloLinkWebPort = 80);

public sealed record DevicePlan(string DeviceId, string CurrentIp, string TargetIp, string Hostname, DeviceRole Role, bool ExistingStaticDevice = false);
public sealed record OnboardingPlan(OnboardingRequest Settings, IReadOnlyList<DevicePlan> Devices, IReadOnlyList<string> OccupiedAddresses, IReadOnlyList<string> Warnings);

public sealed record OnboardingStep(string DeviceId, string IpAddress, string Step, string Status, string? Message = null);
public sealed record OnboardingProgress(Guid RunId, string Status, int Completed, int Total, IReadOnlyList<OnboardingStep> Steps, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc = null);

public sealed record RoleUpdate(DeviceRole Role);
public sealed record IdentityUpdate(string Hostname, string NdiChannelName);
public sealed record HdmiProbeResult(bool Connected, string? NegotiatedResolution);
public sealed record TitleCardSource(string Name, string Group, string LocalAddress);

public static class InputValidation
{
    public static IPAddress Ip(string value, string field)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException($"{field} must be a valid IPv4 address.");
        return ip;
    }

    public static void Validate(OnboardingRequest request)
    {
        var start = Ip(request.StaticStart, "Static range start");
        var end = Ip(request.StaticEnd, "Static range end");
        Ip(request.KiloLinkServerIp, "KiloLink Server IP");
        Ip(request.NdiDiscoveryServerIp, "NDI Discovery Server IP");
        Ip(request.SubnetMask, "Subnet mask");
        if (!string.IsNullOrWhiteSpace(request.Gateway)) Ip(request.Gateway, "Gateway");
        if (NetworkAddressing.ToUInt(start) > NetworkAddressing.ToUInt(end))
            throw new ArgumentException("Static range start must be before or equal to its end.");
        if (NetworkAddressing.ToUInt(end) - NetworkAddressing.ToUInt(start) > 4095)
            throw new ArgumentException("The static range is limited to 4096 addresses per onboarding run.");
        if (string.IsNullOrWhiteSpace(request.JobName)) throw new ArgumentException("Job Name is required.");
        if (request.JobName.Contains(',')) throw new ArgumentException("Job Name cannot contain a comma because it is also used as an NDI group name.");
        if (request.KiloLinkPort is < 1 or > 65535) throw new ArgumentException("KiloLink port is invalid.");
        if (request.KiloLinkWebPort is < 1 or > 65535) throw new ArgumentException("KiloLink web port is invalid.");
    }
}
