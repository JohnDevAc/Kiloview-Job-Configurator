using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NDIJobConfigurator.Core;

public static class IntegrationIdentity
{
    public static string? JobId(AppState state) => state.LastJob is null ? null : Hash(new { state.ServerId, state.LastJob.JobName, state.LastJob.StartedUtc });
    public static string? Revision(AppState state) => state.LastJob is null ? null : Hash(new
    {
        JobId = JobId(state), state.LastJob, state.SelectedNetworkAdapterId, state.SelectedNetworkAddress,
        Devices = state.Devices.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => new
        { d.Id, d.IpAddress, d.Hostname, d.Role, d.NdiGroup, d.NdiChannelName, d.HdmiOutputResolution, d.IsOnboarded, d.WebPort })
    });
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
}
