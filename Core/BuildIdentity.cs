using System.Reflection;

namespace KiloviewSetup.Core;

public enum SoftwareReleaseChannel
{
    Main,
    Development
}

public static class BuildIdentity
{
    public static string Version { get; } = ResolveVersion();
    public static SoftwareReleaseChannel ReleaseChannel { get; } = ResolveReleaseChannel();

    private static string ResolveVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Split('+', 2)[0];
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    private static SoftwareReleaseChannel ResolveReleaseChannel()
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "ReleaseChannel", StringComparison.OrdinalIgnoreCase))?
            .Value;
        return value?.Equals("Development", StringComparison.OrdinalIgnoreCase) == true
            ? SoftwareReleaseChannel.Development
            : SoftwareReleaseChannel.Main;
    }
}
