# Internal PC onboarding diagnostics

Failed PC onboarding now produces structured diagnostic evidence on the requesting Job Configurator. This is for internal testing: there is no log viewer, report link, or diagnostic payload on the endpoint cards. The UI only shows normal progress, success and actionable failure/retry feedback.

## Evidence and retention

The server stores evidence beneath `onboarding-diagnostics` in its configured application data directory (normally `%LOCALAPPDATA%\NDI Job Configurator`). Each attempt records its hostname, endpoint when known, job identity/revision, selected adapter, original/target addresses, and received reports. A report includes its ID, occurrence/receipt/expiry times, component version, failing stage, preceding stage timestamps, exception type/message and stack information. It does not collect arbitrary files, NDI configuration contents, credentials, environment variables or HTTP bodies. Credential-shaped values and profile paths are redacted in error details on the client and again on the server.

Reports expire **seven days after server receipt**. Cleanup runs on startup, hourly while running, and before diagnostic reads/writes. Expired reports are removed individually even when a newer report in the same attempt remains. Repeated delivery of the same report does not extend retention. While the server is stopped, files are removed on its next startup; expired reports are never served. The store also caps itself at 512 attempts and four reports per attempt, evicting the oldest attempt files under pressure. Requests are limited to 32 KiB, including chunked uploads.

The PC Agent saves undelivered reports beside its own configuration under `onboarding-diagnostics`. The queue survives Setup/Agent restarts, retains at most 64 reports for seven days from capture, and removes a report only after the server returns its matching report ID. Delivery uses the selected adapter, bypasses proxies, does not follow redirects, and is bounded to eight seconds per try. Failed attempts back off for a minute; one offline server cannot starve a different server. Diagnostic delivery does not hold the Setup operation lock or delay durable outcome reconciliation.

## Paths covered

- Remote Setup: adapter selection, configuration fetch/validation, network plan, NDI preflight/version lookup, recovery snapshot, Windows network application, NDI application/readback, agent installation/configuration, registration and final confirmation exceptions. Recovery steps and failures are retained.
- Agent: UAC/Setup launch failures and abnormal/nonzero Setup exits. Setup failures keep their single local result dialog; recording diagnostics does not add a second dialog.
- Server-local Setup: structured failure details return through the existing schema-1 stdout response. The server saves them without requiring a network upload from itself. Older companions remain compatible and get a server-side exception/process report. Failures before Setup starts, malformed/missing output, timeout and exit-code errors are also retained.

The remote server persists the diagnostic attempt before sending the approval request. This permits evidence from failures before configuration fetch, after rollback, and after server restart. Uploads must match the stored endpoint/attempt and originate from its original address, requested static target, or currently discovered endpoint address, within the existing production-network management boundary. Diagnostics grant no configuration permission and cannot change registration, job membership or a durable onboarding outcome. Old-job reports remain useful evidence without changing the current job.

## Internal retrieval

Read these endpoints using **localhost on the server**; remote retrieval returns 403:

```powershell
$reports = Invoke-RestMethod 'http://127.0.0.1:8091/api/pc-onboarding/diagnostics'
$reports.reports | Select-Object hostname, jobName, stage, receivedUtc, reportId
$id = $reports.reports[0].reportId
(Invoke-WebRequest "http://127.0.0.1:8091/api/pc-onboarding/diagnostics/$id" -UseBasicParsing).Content
```

`GET /api/pc-onboarding/diagnostics?endpointId=<guid>` filters the report index. The report endpoint returns plain text JSON. Missing or expired reports return 404. The client ingestion endpoint is `POST /api/pc-onboarding/diagnostics` and acknowledges `{reportId, expiresUtc}` after the atomic disk write.

## Validation

Use isolated state/configuration paths; never trigger live onboarding merely to test the logger.

```powershell
dotnet run --project .\tests\Regression\Regression.csproj --configuration Release
node --test .\tests\frontend.test.mjs
powershell.exe -NoProfile -File .\tests\installer.Tests.ps1
powershell.exe -NoProfile -File .\tests\onboarding-interop.Tests.ps1
powershell.exe -NoProfile -File .\tests\onboarding-diagnostics-http.Tests.ps1
```

Also run both companion validation projects documented in its handoff. Coverage includes queue restart/retry/fairness, mismatched acknowledgements, source/attempt rejection, pre-fetch failures after server restart, fixed/chunked upload bounds, redaction, individual report expiry, server-local prerequisite capture, persistent UI errors, duplicate clicks and success followed by a failed display refresh. The HTTP test starts an isolated server on an ephemeral loopback port with an unavailable adapter, and never invokes Setup or changes Windows/NDI settings.

Both products must be updated to capture full client stages. An old server cannot acknowledge the new upload route; the updated agent retains those reports until delivery succeeds or its queue retention expires. Builds and tests do not install either product.

Validation completed on 7 September 2026: 23 server regression checks, 12 frontend tests, installer package validation, both companion validation projects, 12 cross-repository scenarios, and the real isolated diagnostic HTTP test all passed. Server, companion and bootstrapper builds completed with zero warnings/errors. JavaScript syntax and both Git whitespace checks passed. The actual card renderer/controller was visually checked at 340px card width for idle, disabled/pending and persistent error/retry states using an isolated browser fixture. No software was installed, release published, or live Windows/NDI setting changed. The original installed-pair failure has not been reproduced with these new binaries; its cause remains unconfirmed until that controlled deployment test.
