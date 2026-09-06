using System.Net;

namespace NDIJobConfigurator.Core;

public enum DeviceFamily { N6, N60, TeleTool, Simulated, SimulatedTeleTool }
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
    public string? TunedNdiChannelName { get; init; }
    public string? FirmwareVersion { get; init; }
    public bool IsStatic { get; init; }
    public bool IsOnboarded { get; init; }
    public bool LicenseAccepted { get; init; }
    public bool? HdmiDisplayConnected { get; init; }
    public string? HdmiOutputResolution { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
    public DeviceCredentials Credentials { get; init; } = new();
    public int WebPort { get; init; } = 80;
    public bool CanOnboard { get; init; } = true;
    public string? ManagementState { get; init; }
    public string? ManagementMessage { get; init; }
    public bool? StreamRunning { get; init; }
    public string? StreamStatus { get; init; }
    public string? ActiveChannelName { get; init; }
    public string? ActiveChannelNumber { get; init; }
    public string? PipelineStatus { get; init; }
    public string? RfSignal { get; init; }
    public string? RfSignalKind { get; init; }
    public double? SystemTemperatureC { get; init; }
    public bool? DanteAudioActive { get; init; }
    public bool? DanteAudioReady { get; init; }
    public string? DanteAudioStatus { get; init; }
    public string? DanteAudioDeviceLabel { get; init; }
    public string? DanteAudioDetails { get; init; }
    public string? DanteAudioKind { get; init; }
    public bool TeleToolControlReady { get; init; }
    public string? TeleToolReleaseBranch { get; init; }
    public bool MulticastConfigured { get; init; }
    public bool MulticastInUse { get; init; }
    public string? MulticastNetPrefix { get; init; }
    public string? MulticastNetmask { get; init; }
    public int? MulticastTtl { get; init; }
    public string? MulticastLastError { get; init; }
}

public static class DeviceClassification
{
    public static bool IsTeleTool(this ManagedDevice device) =>
        device.Family is DeviceFamily.TeleTool or DeviceFamily.SimulatedTeleTool;

    public static bool IsSimulation(this ManagedDevice device) =>
        device.Family is DeviceFamily.Simulated or DeviceFamily.SimulatedTeleTool;

    public static bool IsKiloview(this ManagedDevice device) => !device.IsTeleTool();
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
public sealed record KiloLinkConnectionRequest(string ServerIp, int WebPort, string Username, string Password, string? JobName = null);
public sealed record KiloLinkConnectionStatus(
    string Version,
    IReadOnlyList<string> DeviceTypes,
    IReadOnlyList<string> FirmwareTypes,
    int DeviceCount,
    bool PasswordChanged = false,
    bool UsedFactoryCredentials = false);
public sealed record KiloLinkServerDiscovery(string ServerIp, int WebPort, string Version);
public sealed record NdiDiscoveryServerDiscovery(string ServerIp, int Port);
public sealed record KiloLinkAuthorizationResult(string SerialNumber, string Hostname, string AuthorizationCode, bool Created);
public sealed record KiloLinkFleetResult(int PackagesUploaded, int DevicesDispatched, IReadOnlyList<string> Models);
public sealed record KiloLinkClearResult(int DevicesDeleted, int GroupsDeleted);

public sealed record AppState(
    IReadOnlyList<ManagedDevice> Devices,
    LastJob? LastJob = null,
    FirmwareJob? FirmwareJob = null,
    SoftwareReleaseChannel UpdateChannel = SoftwareReleaseChannel.Main,
    string? TeleToolManagerId = null,
    MulticastConfiguration? Multicast = null,
    string? SelectedNetworkAdapterId = null,
    string? SelectedNetworkAddress = null,
    IReadOnlyList<WindowsPcEndpoint>? WindowsPcs = null)
{
    public static AppState Empty => new([]);
    public Guid ServerId { get; init; }
    public IReadOnlyList<WindowsPcOnboardingReceipt>? PcOnboardingReceipts { get; init; }
    public int IntegrationSchemaVersion => 1;
    public string? JobId => IntegrationIdentity.JobId(this);
    public string? JobRevision => IntegrationIdentity.Revision(this);
}

public sealed record DiscoveryRequest(
    DeviceCredentials? Credentials,
    bool Simulation = false,
    bool CleanOnboarding = false);

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
    int KiloLinkWebPort = 80,
    string Dns = "8.8.8.8",
    bool CleanOnboarding = false,
    bool IncludeServerPc = false);

public sealed record DevicePlan(
    string DeviceId,
    string CurrentIp,
    string TargetIp,
    string Hostname,
    DeviceRole Role,
    bool ExistingStaticDevice = false,
    DeviceFamily Family = DeviceFamily.N6);
public sealed record OnboardingPlan(
    Guid PlanId,
    OnboardingRequest Settings,
    IReadOnlyList<DevicePlan> Devices,
    IReadOnlyList<string> OccupiedAddresses,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ExpiresUtc);
public sealed record OnboardingRunRequest(Guid PlanId);

public sealed record OnboardingStep(string DeviceId, string IpAddress, string Step, string Status, string? Message = null);
public sealed record OnboardingProgress(Guid RunId, string Status, int Completed, int Total, IReadOnlyList<OnboardingStep> Steps, DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc = null);

public sealed record RoleUpdate(DeviceRole Role);
public sealed record TeleToolRemovalResult(string Id, string Hostname, int ManagedTeleToolCount, string FleetStatus);
public sealed record KiloviewRemovalResult(string Id, string Hostname, int ManagedKiloviewCount, bool KiloLinkRecordRemoved, string Status);
public sealed record IdentityUpdate(string Hostname, string NdiChannelName);
public sealed record HdmiInputProbeResult(bool SignalPresent, string? Resolution);
public sealed record HdmiProbeResult(bool Connected, string? NegotiatedResolution);
public sealed record TitleCardSource(string Name, string Group, string LocalAddress);
public sealed record MulticastSetupRequest(
    int Ttl = 1,
    bool Regenerate = false);
public sealed record NetworkAdapterSelection(string AdapterId, string Address);
public sealed record LocalNetworkInterface(
    string Id,
    string Name,
    string Description,
    string Address,
    int PrefixLength,
    string Type);
public sealed record WindowsPcEndpoint(
    string EndpointId,
    string Hostname,
    string Address,
    string AdapterName,
    int PrefixLength,
    bool PreferredInterfaceConfigured,
    string NdiToolsVersion,
    string UtilityVersion,
    string EulaVersion,
    DateTimeOffset RegisteredUtc,
    DateTimeOffset LastSeenUtc,
    string Status,
    string? Error = null,
    DateTimeOffset? LastConnectivityCheckUtc = null,
    int ConsecutiveConnectivityFailures = 0,
    string ConnectivityStatus = "unknown",
    string? OperatingSystemVersion = null,
    int? AgentSchemaVersion = null,
    string? AgentVersion = null,
    IReadOnlyList<string>? AgentCapabilities = null,
    long? AgentUptimeSeconds = null,
    long? MachineUptimeSeconds = null,
    long? PhysicalMemoryTotalBytes = null,
    long? PhysicalMemoryAvailableBytes = null,
    long? SystemDriveTotalBytes = null,
    long? SystemDriveFreeBytes = null,
    DateTimeOffset? AgentObservedUtc = null,
    bool IsServerPc = false,
    string? RegistrationAttemptId = null,
    string? RegistrationJobId = null,
    string? RegistrationJobRevision = null);
public sealed record WindowsPcRegistration(
    string EndpointId,
    string Hostname,
    string Address,
    string AdapterName,
    int PrefixLength,
    bool PreferredInterfaceConfigured,
    string NdiToolsVersion,
    string UtilityVersion,
    string EulaVersion,
    string? OperatingSystemVersion = null,
    string? AttemptId = null,
    string? JobId = null,
    string? JobRevision = null);
public sealed record WindowsPcOnboardingReceipt(
    string EndpointId, string AttemptId, string JobId, string JobRevision,
    string OriginalAddress, WindowsPcRemoteNetworkConfiguration Network,
    DateTimeOffset StartedUtc, DateTimeOffset RegistrationDeadlineUtc, string Status,
    WindowsPcEndpoint? Candidate = null);
public sealed record WindowsPcOnboardingOutcome(
    string EndpointId, string AttemptId, string JobId, string JobRevision, string Outcome);
public sealed record WindowsPcOnboardingOutcomeResult(
    string AttemptId, string JobId, string JobRevision, string Status);
public sealed record WindowsPcAgentHealth(string Status, string Product, string Version, int SchemaVersion);
public static class WindowsPcAgentContract
{
    public const string ProductName = "NDI Configurator PC Agent";
    public const string LegacyProductName = "Kiloview PC Agent";

    public static bool IsCompatibleProduct(string? product) =>
        string.Equals(product, ProductName, StringComparison.Ordinal)
        || string.Equals(product, LegacyProductName, StringComparison.Ordinal);
}
public sealed record WindowsPcAgentMembership(
    string ServerAddress,
    string ConfiguratorUrl,
    string JobName,
    DateTimeOffset RegisteredUtc);
public sealed record WindowsPcAgentMemberships(
    string EndpointId,
    IReadOnlyList<WindowsPcAgentMembership> Memberships);
public sealed record WindowsPcAgentNetworkConfiguration(
    bool? DhcpEnabled,
    IReadOnlyList<string>? DefaultGateways,
    IReadOnlyList<string>? DnsServers);
public sealed record WindowsPcAgentMulticastConfiguration(
    string Mode,
    string AdapterId,
    bool SendEnabled,
    bool ReceiveEnabled,
    string? NetPrefix,
    string? Netmask,
    int? Ttl,
    string? JobName,
    bool InUse,
    DateTimeOffset ObservedUtc);
public sealed record WindowsPcAgentDiscovery(
    int SchemaVersion,
    string Product,
    string AgentVersion,
    string EndpointId,
    string Hostname,
    string Address,
    int PrefixLength,
    int ApiPort,
    string Status,
    int MembershipCount,
    IReadOnlyList<string> Capabilities);
public sealed record WindowsPcAgentStatus(
    int SchemaVersion,
    string Product,
    string AgentVersion,
    string Status,
    string EndpointId,
    string Hostname,
    string OperatingSystemVersion,
    string Address,
    int PrefixLength,
    string AdapterId,
    string AdapterName,
    bool NdiToolsInstalled,
    string? NdiToolsVersion,
    DateTimeOffset AgentStartedUtc,
    long AgentUptimeSeconds,
    long MachineUptimeSeconds,
    long PhysicalMemoryTotalBytes,
    long PhysicalMemoryAvailableBytes,
    long SystemDriveTotalBytes,
    long SystemDriveFreeBytes,
    IReadOnlyList<WindowsPcAgentMembership> Memberships,
    DateTimeOffset ObservedUtc,
    WindowsPcAgentNetworkConfiguration? NetworkConfiguration = null,
    WindowsPcAgentMulticastConfiguration? MulticastConfiguration = null,
    WindowsPcNdiConfiguration? NdiConfiguration = null);
public sealed record WindowsPcNdiConfiguration(bool PreferredInterfaceConfigured,
    IReadOnlyList<string> SendGroups, IReadOnlyList<string> ReceiveGroups, string DiscoveryServer);
public sealed record WindowsPcAgentSnapshot(
    string EndpointId,
    string Hostname,
    string Address,
    int PrefixLength,
    int ApiPort,
    string AgentVersion,
    IReadOnlyList<string> Capabilities,
    string Status,
    string OperatingSystemVersion,
    string AdapterId,
    string AdapterName,
    bool NdiToolsInstalled,
    string? NdiToolsVersion,
    DateTimeOffset AgentStartedUtc,
    long AgentUptimeSeconds,
    long MachineUptimeSeconds,
    long PhysicalMemoryTotalBytes,
    long PhysicalMemoryAvailableBytes,
    long SystemDriveTotalBytes,
    long SystemDriveFreeBytes,
    IReadOnlyList<WindowsPcAgentMembership> Memberships,
    DateTimeOffset ObservedUtc,
    DateTimeOffset LastDiscoveredUtc,
    WindowsPcAgentNetworkConfiguration? NetworkConfiguration = null,
    WindowsPcAgentMulticastConfiguration? MulticastConfiguration = null);
public sealed record WindowsPcAgentOpenRequest(
    string ServerName,
    string ServerAddress,
    string JobName,
    string ConfiguratorUrl,
    string? AttemptId = null);
public sealed record WindowsPcRemoteOnboardingRequest(
    WindowsPcRemoteNetworkRequest Network);
public sealed record WindowsPcRemoteNetworkRequest(
    string Mode,
    string? AdapterId = null,
    string? Address = null,
    int? PrefixLength = null,
    string? DefaultGateway = null,
    IReadOnlyList<string>? DnsServers = null);
public sealed record WindowsPcRemoteNetworkConfiguration(
    string AdapterId,
    string Mode,
    string? Address = null,
    int? PrefixLength = null,
    string? DefaultGateway = null,
    IReadOnlyList<string>? DnsServers = null);
public sealed record WindowsPcRemoteOnboardingConfiguration(
    int SchemaVersion,
    string Product,
    string EndpointId,
    string JobName,
    string NdiDiscoveryServerIp,
    WindowsPcRemoteNetworkConfiguration? Network,
    string? AttemptId = null,
    string? JobId = null,
    string? JobRevision = null,
    bool RequiresFinalConfirmation = true);
public sealed record WindowsPcRemoteOnboardingState(
    string EndpointId,
    string Status,
    string Message,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? RegistrationDeadlineUtc,
    DateTimeOffset ExpiresUtc,
    string? RegisteredAddress = null,
    DateTimeOffset? ConfigurationFetchedUtc = null,
    string? AttemptId = null);
public sealed record WindowsPcAgentMulticastRequest(
    int SchemaVersion,
    string EndpointId,
    string JobName,
    string AdapterId,
    string Mode,
    bool SendEnabled,
    bool ReceiveEnabled,
    string? NetPrefix = null,
    string? Netmask = null,
    int? Ttl = null);
public sealed record WindowsPcAgentMulticastResult(
    int SchemaVersion,
    string Product,
    string EndpointId,
    string Mode,
    string AdapterId,
    bool SendEnabled,
    bool ReceiveEnabled,
    string? NetPrefix,
    string? Netmask,
    int? Ttl,
    string? JobName,
    bool InUse,
    DateTimeOffset ObservedUtc);
public sealed record MulticastDeviceConfiguration(
    string Group,
    string? NetPrefix,
    string? Netmask,
    int Ttl,
    IReadOnlyList<string> SenderAddresses);
public sealed record MulticastAssignment(
    string EndpointId,
    string Hostname,
    string Address,
    string Family,
    DeviceRole Role,
    bool Sender,
    bool Receiver,
    string? NetPrefix,
    string? Netmask,
    int Ttl,
    string Status = "planned",
    bool InUse = false,
    string? Error = null);
public sealed record MulticastConfiguration(
    Guid PlanId,
    string JobName,
    string PoolPrefix,
    string PoolNetmask,
    string PoolLastAddress,
    string AllocationNetmask,
    int Ttl,
    IReadOnlyList<MulticastAssignment> Assignments,
    string Status,
    DateTimeOffset GeneratedUtc,
    DateTimeOffset? AppliedUtc = null);
public sealed record MulticastApplyResult(
    string Status,
    int Applied,
    int Failed,
    MulticastConfiguration Configuration);
public sealed record MulticastRevertResult(
    string Status,
    int Reverted,
    int Failed,
    MulticastConfiguration? Configuration,
    IReadOnlyList<string> Errors);

public static class InputValidation
{
    public static IPAddress Ip(string value, string field)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException($"{field} must be a valid IPv4 address.");
        return ip;
    }

    public static void Validate(OnboardingRequest request, bool requireKiloLink = true)
    {
        var start = Ip(request.StaticStart, "Static range start");
        var end = Ip(request.StaticEnd, "Static range end");
        var subnetMask = Ip(request.SubnetMask, "Subnet mask");
        if (requireKiloLink) Ip(request.KiloLinkServerIp, "KiloLink Server IP");
        Ip(request.NdiDiscoveryServerIp, "NDI Discovery Server IP");
        var gateway = string.IsNullOrWhiteSpace(request.Gateway) ? null : Ip(request.Gateway, "Gateway");
        Ip(request.Dns, "DNS server");
        if (NetworkAddressing.ToUInt(start) > NetworkAddressing.ToUInt(end))
            throw new ArgumentException("Static range start must be before or equal to its end.");
        if (NetworkAddressing.ToUInt(end) - NetworkAddressing.ToUInt(start) > 4095)
            throw new ArgumentException("The static range is limited to 4096 addresses per onboarding run.");
        var mask = NetworkAddressing.ToUInt(subnetMask);
        var prefixLength = NetworkAddressing.GetPrefixLength(mask);
        if (prefixLength is < 1 or > 30)
            throw new ArgumentException("Subnet mask must define a usable IPv4 subnet between /1 and /30.");
        var network = NetworkAddressing.ToUInt(start) & mask;
        var broadcast = network | ~mask;
        var startNumber = NetworkAddressing.ToUInt(start);
        var endNumber = NetworkAddressing.ToUInt(end);
        if ((endNumber & mask) != network)
            throw new ArgumentException("Static range start and end must be in the same subnet.");
        if (startNumber == network || startNumber == broadcast || endNumber == network || endNumber == broadcast)
            throw new ArgumentException("Static range cannot include the subnet network or broadcast address.");
        if (gateway is not null)
        {
            var gatewayNumber = NetworkAddressing.ToUInt(gateway);
            if ((gatewayNumber & mask) != network)
                throw new ArgumentException("Gateway must be in the same subnet as the static range.");
            if (gatewayNumber == network || gatewayNumber == broadcast)
                throw new ArgumentException("Gateway cannot be the subnet network or broadcast address.");
            if (gatewayNumber >= startNumber && gatewayNumber <= endNumber)
                throw new ArgumentException("Gateway cannot be inside the device static range.");
        }
        if (string.IsNullOrWhiteSpace(request.JobName)) throw new ArgumentException("Job Name is required.");
        if (request.JobName.Contains(',')) throw new ArgumentException("Job Name cannot contain a comma because it is also used as an NDI group name.");
        if (requireKiloLink && request.KiloLinkPort is < 1 or > 65535) throw new ArgumentException("KiloLink port is invalid.");
        if (requireKiloLink && request.KiloLinkWebPort is < 1 or > 65535) throw new ArgumentException("KiloLink web port is invalid.");
    }
}
