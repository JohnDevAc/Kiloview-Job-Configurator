using System.Text.RegularExpressions;

namespace KiloviewSetup.Core;

/// <summary>
/// Verifies an existing KiloLink login and completes the official first-login password change
/// when a server is still using its factory administrator credential.
/// </summary>
public sealed partial class KiloLinkConnectionService(
    KiloLinkCredentialStore credentials,
    KiloLinkServerClient client)
{
    private static readonly KiloLinkCredential FactoryCredential = new("admin", "Kiloview001");
    private sealed record CredentialCandidate(KiloLinkCredential Credential, bool StoreOnSuccess);

    public async Task<KiloLinkConnectionStatus> ConnectAsync(KiloLinkConnectionRequest request, CancellationToken ct)
    {
        InputValidation.Ip(request.ServerIp, "KiloLink Server IP");
        if (request.WebPort is < 1 or > 65535) throw new ArgumentException("KiloLink web port is invalid.");

        var candidates = CandidateCredentials(request).ToArray();
        Exception? previousFailure = null;
        foreach (var candidate in candidates)
        {
            if (IsFactoryCredential(candidate.Credential))
            {
                try
                {
                    _ = await client.AuthenticateAsync(request.ServerIp, request.WebPort, candidate.Credential, ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
                {
                    previousFailure = ex;
                    continue;
                }
                return await ProvisionFactoryServerAsync(request, ct);
            }

            KiloLinkConnectionStatus status;
            try
            {
                status = await client.TestAsync(request.ServerIp, request.WebPort, candidate.Credential, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                previousFailure = ex;
                continue;
            }
            if (candidate.StoreOnSuccess)
                credentials.StoreVerified(request.ServerIp, candidate.Credential);
            return status;
        }

        try
        {
            _ = await client.AuthenticateAsync(request.ServerIp, request.WebPort, FactoryCredential, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            var reason = candidates.Length > 0
                ? "The supplied or stored KiloLink login was rejected, and the official factory login admin/Kiloview001 was not accepted."
                : "No stored KiloLink login exists, and the official factory login admin/Kiloview001 was not accepted.";
            throw new InvalidOperationException($"{reason} Enter the server's current administrator credentials.", previousFailure ?? ex);
        }
        return await ProvisionFactoryServerAsync(request, ct);
    }

    private async Task<KiloLinkConnectionStatus> ProvisionFactoryServerAsync(
        KiloLinkConnectionRequest request,
        CancellationToken ct)
    {
        var jobName = request.JobName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(jobName))
            throw new InvalidOperationException(
                "KiloLink Server is using its factory login. Enter the Job Name below; it will become the new KiloLink administrator password.");
        if (!KiloLinkPasswordPattern().IsMatch(jobName))
            throw new ArgumentException(
                "The Job Name must be 6–32 characters and contain an uppercase letter, lowercase letter, and number before it can be used as the KiloLink password.");

        await client.ChangeInitialPasswordAsync(request.ServerIp, request.WebPort, FactoryCredential, jobName, ct);
        var provisionedCredential = new KiloLinkCredential(FactoryCredential.Username, jobName);
        var verified = await client.TestAsync(request.ServerIp, request.WebPort, provisionedCredential, ct);
        credentials.StoreVerified(request.ServerIp, provisionedCredential);
        return verified with { PasswordChanged = true, UsedFactoryCredentials = true };
    }

    private IEnumerable<CredentialCandidate> CandidateCredentials(KiloLinkConnectionRequest request)
    {
        if (!string.IsNullOrEmpty(request.Password))
        {
            if (string.IsNullOrWhiteSpace(request.Username))
                throw new ArgumentException("KiloLink server username is required when entering a password.");
            yield return new(new(request.Username.Trim(), request.Password), StoreOnSuccess: true);
            yield break;
        }

        if (!credentials.GetStatus(request.ServerIp).Stored) yield break;
        var stored = credentials.GetStoredCredential(request.ServerIp);
        if (!string.IsNullOrWhiteSpace(request.Username) &&
            !string.Equals(request.Username.Trim(), stored.Username, StringComparison.Ordinal))
            throw new ArgumentException("Enter the password for the new KiloLink username, or use the stored username shown by the application.");
        yield return new(stored, StoreOnSuccess: false);
    }

    private static bool IsFactoryCredential(KiloLinkCredential credential) =>
        string.Equals(credential.Username, FactoryCredential.Username, StringComparison.Ordinal) &&
        string.Equals(credential.Password, FactoryCredential.Password, StringComparison.Ordinal);

    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)[A-Za-z\d!@#$%^&*()_+\-=\[\]{};':""\\|,.<>/?]{6,32}$")]
    private static partial Regex KiloLinkPasswordPattern();
}
