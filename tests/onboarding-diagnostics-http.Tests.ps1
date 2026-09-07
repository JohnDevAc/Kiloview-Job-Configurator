param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path ([IO.Path]::GetTempPath()) ('ndi-diagnostic-http-' + [guid]::NewGuid().ToString('N'))
$diagnosticDirectory = Join-Path $data 'onboarding-diagnostics'
New-Item -ItemType Directory -Path $diagnosticDirectory -Force | Out-Null
$attemptId = [guid]::NewGuid().ToString('D')
$endpointId = [guid]::NewGuid().ToString('D')
$reportId = [guid]::NewGuid().ToString('D')
$scope = @{ endpointId=$endpointId; attemptId=$attemptId; hostname='HTTP fixture'; jobId='fixture'; jobRevision='fixture';
    jobName='Diagnostic fixture'; originalAddress='127.0.0.1'; adapterId='unavailable-fixture'; isServerPc=$false;
    createdUtc=[datetimeoffset]::UtcNow.ToString('O'); expiresUtc=[datetimeoffset]::UtcNow.AddDays(7).ToString('O'); reports=@() }
$scope | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $diagnosticDirectory ($attemptId + '.json')) -Encoding UTF8
$fixture = @{ devices=@(); selectedNetworkAdapterId='unavailable-fixture'; selectedNetworkAddress='192.0.2.10' }
$fixture | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'state.json') -Encoding UTF8
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command dotnet).Source
$start.Arguments = '"' + (Join-Path $root 'bin\Debug\net8.0-windows\NDIJobConfigurator.dll') + '"'
$start.WorkingDirectory = $root
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
$start.EnvironmentVariables['NDI_JOB_CONFIGURATOR_DATA_DIR'] = $data
$start.EnvironmentVariables['NDI_JOB_CONFIGURATOR_NDI_CONFIG_PATH'] = Join-Path $data 'ndi.json'
$start.EnvironmentVariables['NDI_JOB_CONFIGURATOR_SERVICE_PORT'] = [string]$port
$start.EnvironmentVariables['NDI_JOB_CONFIGURATOR_LAN_ACCESS'] = '0'
$start.EnvironmentVariables['KILOVIEW_LAN_ACCESS'] = '0'
$process = [Diagnostics.Process]::Start($start)
$url = "http://127.0.0.1:$port"
try {
    $ready = $false
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Fixture server exited: $($process.ExitCode)" }
        try { Invoke-RestMethod "$url/api/health" -TimeoutSec 2 | Out-Null; $ready=$true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Fixture server did not start.' }
    $report = @{schemaVersion=1;reportId=$reportId;endpointId=$endpointId;attemptId=$attemptId;occurredUtc=[datetimeoffset]::UtcNow.ToString('O');
        version='fixture';stage='ndi-preflight';errorType='IOException';message='password=secret-fixture Access denied';stackTrace='at Fixture.Preflight()';
        entries=@(@{atUtc=[datetimeoffset]::UtcNow.ToString('O');stage='fetch-configuration';message='Configuration received'})}
    $body = $report | ConvertTo-Json -Depth 6
    $receipt = Invoke-RestMethod "$url/api/pc-onboarding/diagnostics" -Method Post -ContentType 'application/json' -Body $body
    if ($receipt.reportId -ne $reportId) { throw 'Diagnostic receipt identity mismatch.' }
    Invoke-RestMethod "$url/api/pc-onboarding/diagnostics" -Method Post -ContentType 'application/json' -Body $body | Out-Null
    $list = Invoke-RestMethod "$url/api/pc-onboarding/diagnostics"
    if ($list.retentionDays -ne 7 -or $list.reports.Count -ne 1) { throw 'HTTP diagnostic retry was not idempotent.' }
    $saved = (Invoke-WebRequest "$url/api/pc-onboarding/diagnostics/$reportId" -UseBasicParsing).Content
    if ($saved -match 'secret-fixture' -or $saved -notmatch 'Configuration received') { throw 'HTTP evidence redaction or stage capture failed.' }
    $report.attemptId = [guid]::NewGuid().ToString('D')
    $code = 0
    try { Invoke-WebRequest "$url/api/pc-onboarding/diagnostics" -Method Post -ContentType 'application/json' -Body ($report|ConvertTo-Json -Depth 6) -UseBasicParsing | Out-Null }
    catch { $code=[int]$_.Exception.Response.StatusCode }
    if ($code -ne 403) { throw "Unknown attempt accepted: $code" }
    $code = 0
    try { Invoke-WebRequest "$url/api/pc-onboarding/diagnostics" -Method Post -ContentType 'application/json' -Body ('x' * 33000) -UseBasicParsing | Out-Null }
    catch { $code=[int]$_.Exception.Response.StatusCode }
    if ($code -ne 413) { throw "Oversized report accepted: $code" }
    # Stream a chunked request without Content-Length to exercise the bounded body reader.
    $tcp = [Net.Sockets.TcpClient]::new('127.0.0.1',$port)
    try {
        $stream = $tcp.GetStream()
        $stream.ReadTimeout=5000
        $payload = "POST /api/pc-onboarding/diagnostics HTTP/1.1`r`nHost: 127.0.0.1:$port`r`nContent-Type: application/json`r`nTransfer-Encoding: chunked`r`nConnection: close`r`n`r`n80E8`r`n" + ('x' * 33000) + "`r`n0`r`n`r`n"
        $bytes = [Text.Encoding]::ASCII.GetBytes($payload)
        $stream.Write($bytes,0,$bytes.Length)
        $reader = [IO.StreamReader]::new($stream)
        if ($reader.ReadLine() -notmatch '413') { throw 'Chunked oversized diagnostic was not rejected.' }
    } finally { $tcp.Dispose() }
    $state = Invoke-RestMethod "$url/api/state"
    if ($state.windowsPcs.Count -gt 0) { throw 'Diagnostic upload changed membership.' }
    $code = 0
    try { Invoke-WebRequest "$url/api/pc-onboarding/local" -Method Post -ContentType 'application/json' -Body '{}' -UseBasicParsing | Out-Null }
    catch { $code=[int]$_.Exception.Response.StatusCode }
    if ($code -ne 409) { throw 'An unavailable fixture adapter did not block local onboarding before Setup.' }
    $list = Invoke-RestMethod "$url/api/pc-onboarding/diagnostics"
    if ($list.reports.Count -ne 2 -or -not ($list.reports | Where-Object { $_.message -match 'create a job first' })) { throw 'Local prerequisite failure was not recorded.' }
    Write-Output "PASS Diagnostic HTTP ingestion, idempotent receipt, redaction, unrelated attempt rejection, fixed/chunked body bounds; isolated data $data"
} finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
