using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NDIJobConfigurator.Core;

public sealed record OnboardingFailureEntry(DateTimeOffset AtUtc, string Stage, string Message);
public sealed record OnboardingFailureReport(int SchemaVersion, string ReportId, string EndpointId, string AttemptId,
    DateTimeOffset OccurredUtc, string Version, string Stage, string ErrorType, string Message, string StackTrace,
    IReadOnlyList<OnboardingFailureEntry> Entries);
public sealed record OnboardingDiagnosticAttempt(string EndpointId, string AttemptId, string Hostname,
    string? JobId, string? JobRevision, string JobName, string OriginalAddress, string? TargetAddress,
    string AdapterId, bool IsServerPc, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc,
    IReadOnlyList<StoredOnboardingFailure> Reports);
public sealed record StoredOnboardingFailure(OnboardingFailureReport Report, DateTimeOffset ReceivedUtc, DateTimeOffset ExpiresUtc);
public sealed record OnboardingFailureSummary(string ReportId, string EndpointId, string AttemptId, string Hostname,
    string JobName, string Stage, string Message, DateTimeOffset ReceivedUtc, DateTimeOffset ExpiresUtc, string Url);

// Diagnostics never grant configuration permission or change an onboarding outcome.
public sealed class OnboardingDiagnosticsStore
{
    public const int RetentionDays = 7;
    public const int MaximumReportBytes = 32 * 1024;
    private const int MaximumAttempts = 512;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly ILogger<OnboardingDiagnosticsStore> _logger;
    private DateTimeOffset Now => _clock.GetUtcNow();

    public OnboardingDiagnosticsStore(IWebHostEnvironment environment, ILogger<OnboardingDiagnosticsStore> logger, TimeProvider? clock = null)
    {
        _directory = Path.Combine(AppDataPaths.ResolveDataDirectory(environment.ContentRootPath), "onboarding-diagnostics");
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public void Register(OnboardingDiagnosticAttempt attempt)
    {
        lock (_gate)
        {
            CleanupUnsafe();
            if (!Guid.TryParse(attempt.AttemptId, out var id) || id == Guid.Empty) throw new ArgumentException("Invalid diagnostic attempt.");
            Write(attempt with { CreatedUtc = Now, ExpiresUtc = Now.AddDays(RetentionDays), Reports = [] });
            CleanupUnsafe();
        }
    }

    public OnboardingFailureSummary SaveRemote(OnboardingFailureReport report, string sourceAddress, Func<string, string, bool> discoveredAt)
    {
        lock (_gate)
        {
            Validate(report);
            CleanupUnsafe();
            var attempt = Read(PathFor(report.AttemptId));
            if (attempt is null || attempt.IsServerPc || attempt.ExpiresUtc <= Now || attempt.EndpointId != report.EndpointId
                || !(sourceAddress == attempt.OriginalAddress || sourceAddress == attempt.TargetAddress || discoveredAt(report.EndpointId, sourceAddress)))
                throw new UnauthorizedAccessException("This source cannot submit diagnostics for that onboarding attempt.");
            return SaveUnsafe(attempt, report);
        }
    }

    public OnboardingFailureSummary SaveLocal(string attemptId, Exception error, OnboardingFailureReport? companionReport = null)
    {
        lock (_gate)
        {
            var attempt = Read(PathFor(attemptId)) ?? throw new IOException("The local diagnostic attempt is unavailable.");
            if (companionReport is not null)
                try { Validate(companionReport); }
                catch (ArgumentException) { companionReport = null; }
            if (string.IsNullOrEmpty(attempt.EndpointId) && companionReport is not null
                && Guid.TryParse(companionReport.EndpointId, out var endpointId) && endpointId != Guid.Empty)
                attempt = attempt with { EndpointId = endpointId.ToString("D") };
            var report = companionReport is null
                ? new OnboardingFailureReport(1, Guid.NewGuid().ToString("D"), attempt.EndpointId, attemptId, Now,
                    BuildIdentity.Version, "server-local-command", error.GetType().Name, error.Message,
                    new StackTrace(error, false).ToString(), [])
                : companionReport with { EndpointId = attempt.EndpointId, AttemptId = attemptId };
            return SaveUnsafe(attempt, Sanitize(report));
        }
    }

    public IReadOnlyList<OnboardingFailureSummary> List(string? endpointId = null)
    {
        lock (_gate)
        {
            CleanupUnsafe();
            return ReadAll().Where(a => endpointId is null || a.EndpointId == endpointId)
                .SelectMany(a => a.Reports.Where(r => r.ExpiresUtc > Now).Select(r => Summary(a, r)))
                .OrderByDescending(r => r.ReceivedUtc).ToArray();
        }
    }

    public string? ReportText(string reportId)
    {
        lock (_gate)
        {
            CleanupUnsafe();
            foreach (var attempt in ReadAll())
                if (attempt.Reports.FirstOrDefault(r => r.Report.ReportId == reportId && r.ExpiresUtc > Now) is { } stored)
                    return JsonSerializer.Serialize(new
                    {
                        retentionDays = RetentionDays,
                        attempt.Hostname,
                        attempt.JobName,
                        attempt.JobId,
                        attempt.JobRevision,
                        attempt.OriginalAddress,
                        attempt.TargetAddress,
                        attempt.AdapterId,
                        attempt.IsServerPc,
                        stored.ReceivedUtc,
                        stored.ExpiresUtc,
                        stored.Report
                    }, Json);
            return null;
        }
    }

    public void Cleanup() { lock (_gate) CleanupUnsafe(); }

    private OnboardingFailureSummary SaveUnsafe(OnboardingDiagnosticAttempt attempt, OnboardingFailureReport report)
    {
        Validate(report);
        var duplicate = attempt.Reports.FirstOrDefault(r => r.Report.ReportId == report.ReportId);
        if (duplicate is not null) return Summary(attempt, duplicate);
        if (attempt.Reports.Count >= 4) throw new InvalidOperationException("This attempt already has four diagnostic reports.");
        var stored = new StoredOnboardingFailure(Sanitize(report), Now, Now.AddDays(RetentionDays));
        Write(attempt with { ExpiresUtc = stored.ExpiresUtc, Reports = attempt.Reports.Append(stored).ToArray() });
        _logger.LogInformation("Saved onboarding failure {ReportId} for {EndpointId}, attempt {AttemptId}, stage {Stage}; expires {ExpiresUtc}",
            report.ReportId, attempt.EndpointId, attempt.AttemptId, report.Stage, stored.ExpiresUtc);
        return Summary(attempt, stored);
    }

    private static OnboardingFailureSummary Summary(OnboardingDiagnosticAttempt a, StoredOnboardingFailure r) =>
        new(r.Report.ReportId, a.EndpointId, a.AttemptId, a.Hostname, a.JobName, r.Report.Stage, r.Report.Message,
            r.ReceivedUtc, r.ExpiresUtc, $"/api/pc-onboarding/diagnostics/{r.Report.ReportId}");

    private static void Validate(OnboardingFailureReport r)
    {
        if (r.SchemaVersion != 1 || !Guid.TryParse(r.ReportId, out var id) || id == Guid.Empty
            || !Guid.TryParse(r.AttemptId, out id) || id == Guid.Empty || r.EndpointId is null
            || r.Version is null || r.Stage is null || r.ErrorType is null || r.Message is null || r.StackTrace is null
            || r.Entries is null || r.Entries.Count > 32 || r.Entries.Any(e => e is null || e.Stage is null || e.Message is null)
            || JsonSerializer.SerializeToUtf8Bytes(r, Json).Length > MaximumReportBytes)
            throw new ArgumentException("Invalid or oversized onboarding failure report.");
    }

    public static string Redact(string? text, int limit = 2048)
    {
        var value = (text ?? "");
        if (value.Length > 32 * 1024) value = value[..(32 * 1024)];
        value = Regex.Replace(value, @"(?i)Bearer\s+[^\s""']+", "Bearer [redacted]");
        value = Regex.Replace(value, @"(?i)(password|passwd|authorization|access[_-]?token|refresh[_-]?token|secret)(\s*[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;}]+)", "$1$2[redacted]");
        value = Regex.Replace(value, @"(?i)Bearer\s+[^\s""']+", "Bearer [redacted]");
        value = Regex.Replace(value, @"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", "$1[redacted]@");
        value = Regex.Replace(value, @"(?i)[A-Z]:\\Users\\[^\\\s]+", @"%USERPROFILE%");
        return value.Length <= limit ? value : value[..limit] + "…";
    }

    private static OnboardingFailureReport Sanitize(OnboardingFailureReport r) => r with
    {
        Version = Redact(r.Version, 80),
        Stage = Redact(r.Stage, 80),
        ErrorType = Redact(r.ErrorType, 160),
        Message = Redact(r.Message),
        StackTrace = Redact(r.StackTrace, 6000),
        Entries = r.Entries.Take(32).Select(e => e with { Stage = Redact(e.Stage, 80), Message = Redact(e.Message, 256) }).ToArray()
    };

    private string PathFor(string id) => Path.Combine(_directory, Guid.Parse(id).ToString("D") + ".json");
    private OnboardingDiagnosticAttempt? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 160 * 1024) return null;
            var attempt = JsonSerializer.Deserialize<OnboardingDiagnosticAttempt>(File.ReadAllText(path), Json);
            return attempt is not null && Guid.TryParse(attempt.AttemptId, out _)
                && attempt.Reports is not null && attempt.Reports.Count <= 4
                && attempt.Reports.All(r => r?.Report is not null) ? attempt : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { _logger.LogWarning("Could not read diagnostic file {File}: {Error}", Path.GetFileName(path), ex.Message); return null; }
    }
    private IEnumerable<OnboardingDiagnosticAttempt> ReadAll() => Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory, "*.json").Select(Read).OfType<OnboardingDiagnosticAttempt>() : [];

    private void Write(OnboardingDiagnosticAttempt attempt)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(attempt.AttemptId);
        var temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, attempt, Json); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void CleanupUnsafe()
    {
        if (!Directory.Exists(_directory)) return;
        var files = new DirectoryInfo(_directory).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        foreach (var file in files)
        {
            var attempt = file.Extension == ".json" ? Read(file.FullName) : null;
            if (attempt is not null && attempt.ExpiresUtc <= Now
                || attempt is null && file.LastWriteTimeUtc < Now.UtcDateTime.AddDays(-RetentionDays)
                || Array.IndexOf(files, file) >= MaximumAttempts)
                file.Delete();
            else if (attempt is not null && attempt.Reports.Any(r => r.ExpiresUtc <= Now))
                Write(attempt with { Reports = attempt.Reports.Where(r => r.ExpiresUtc > Now).ToArray() });
        }
    }
}

public sealed class OnboardingDiagnosticsCleanup(OnboardingDiagnosticsStore store, ILogger<OnboardingDiagnosticsCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { store.Cleanup(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "Onboarding diagnostic cleanup failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
