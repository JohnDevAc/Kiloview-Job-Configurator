using Microsoft.Extensions.Logging.Abstractions;
using NDIJobConfigurator.Core;

internal static class DiagnosticsRegression
{
    internal static void Run(IWebHostEnvironment environment)
    {
        var clock = new DiagnosticClock();
        var store = new OnboardingDiagnosticsStore(environment, NullLogger<OnboardingDiagnosticsStore>.Instance, clock);
        var endpoint = Guid.NewGuid().ToString("D");
        var attempt = Guid.NewGuid().ToString("D");
        store.Register(new(endpoint, attempt, "Fixture PC", "job", "revision", "Fixture job", "192.0.2.20", "192.0.2.21",
            "adapter", false, clock.GetUtcNow(), clock.GetUtcNow(), []));
        var report = new OnboardingFailureReport(1, Guid.NewGuid().ToString("D"), endpoint, attempt, clock.GetUtcNow(), "test",
            "ndi-preflight", "InvalidOperationException", "password=secret-value Access denied", "at Fixture.Apply()",
            [new(clock.GetUtcNow(), "fetch-configuration", "Settings fetched.")]);
        Reject<UnauthorizedAccessException>(() => store.SaveRemote(report, "192.0.2.99", (_, _) => false));
        Reject<UnauthorizedAccessException>(() => store.SaveRemote(report with { EndpointId = Guid.NewGuid().ToString() }, "192.0.2.20", (_, _) => false));
        Reject<UnauthorizedAccessException>(() => store.SaveRemote(report with { AttemptId = Guid.NewGuid().ToString() }, "192.0.2.20", (_, _) => false));
        Reject<ArgumentException>(() => store.SaveRemote(report with { Message = new string('x', 33000) }, "192.0.2.20", (_, _) => false));
        var saved = store.SaveRemote(report, "192.0.2.21", (_, _) => false);
        Check(!store.ReportText(saved.ReportId)!.Contains("secret-value"), "Credential-shaped data was retained.");
        Check(store.ReportText(saved.ReportId)!.Contains("Settings fetched"), "Stage evidence was lost.");
        clock.Advance(TimeSpan.FromDays(1));
        var duplicate = store.SaveRemote(report, "192.0.2.20", (_, _) => false);
        Check(duplicate == saved && store.List(endpoint).Count == 1, "A retry duplicated or extended the report.");
        var restarted = new OnboardingDiagnosticsStore(environment, NullLogger<OnboardingDiagnosticsStore>.Instance, clock);
        Check(restarted.ReportText(saved.ReportId) is not null, "Server restart lost failure evidence.");
        for (var index = 0; index < 3; index++)
            restarted.SaveRemote(report with { ReportId = Guid.NewGuid().ToString() }, "192.0.2.22", (id, ip) => id == endpoint && ip == "192.0.2.22");
        Reject<InvalidOperationException>(() => restarted.SaveRemote(report with { ReportId = Guid.NewGuid().ToString() }, "192.0.2.20", (_, _) => false));
        clock.Advance(TimeSpan.FromDays(6.5));
        restarted.Cleanup();
        Check(restarted.ReportText(saved.ReportId) is null && restarted.List(endpoint).Count == 3, "Retention did not remove individual expired reports.");
        var retainedFile = Path.Combine(AppDataPaths.ResolveDataDirectory(environment.ContentRootPath), "onboarding-diagnostics", attempt + ".json");
        Check(!File.ReadAllText(retainedFile).Contains(saved.ReportId), "An expired report remained on disk inside a later attempt.");
        clock.Advance(TimeSpan.FromDays(2));
        restarted.Cleanup();
        Check(restarted.ReportText(saved.ReportId) is null && restarted.List(endpoint).Count == 0, "Expired reports remained readable.");
        Reject<UnauthorizedAccessException>(() => restarted.SaveRemote(report, "192.0.2.20", (_, _) => false));

        var localAttempt = Guid.NewGuid().ToString();
        restarted.Register(new("", localAttempt, Environment.MachineName, "job", "rev", "Local job", "192.0.2.5", null, "adapter", true,
            clock.GetUtcNow(), clock.GetUtcNow(), []));
        var local = restarted.SaveLocal(localAttempt, new IOException("Local pipe failed"));
        Check(restarted.ReportText(local.ReportId)!.Contains("Local pipe failed"), "Local process failures were not recorded.");
        Reject<UnauthorizedAccessException>(() => restarted.SaveRemote(report with { EndpointId = "", AttemptId = localAttempt }, "192.0.2.5", (_, _) => true));
        foreach (var sensitive in new[] { "password=hidden", "{\"access_token\":\"hidden\"}", "Authorization: Bearer hidden", "http://user:hidden@example.test/path" })
            Check(!OnboardingDiagnosticsStore.Redact(sensitive).Contains("hidden"), "Secret redaction failed: " + sensitive.Split('=')[0]);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class DiagnosticClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
