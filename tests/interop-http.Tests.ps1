param([string]$ResolumeRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$data = Join-Path ([IO.Path]::GetTempPath()) ('ndi-http-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$statePath = Join-Path $data 'state.json'
$fixture = @{ devices=@(); selectedNetworkAdapterId='isolated-unavailable-adapter'; selectedNetworkAddress='192.0.2.10';
    lastJob=@{jobName='Contract Fixture';staticStart='192.0.2.20';staticEnd='192.0.2.30';ndiDiscoveryServerIp='192.0.2.5';startedUtc='2026-09-06T12:00:00Z'} }
$fixture | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding UTF8
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command dotnet).Source
$dll = Join-Path $root 'bin\Debug\net8.0-windows\NDIJobConfigurator.dll'
if (-not (Test-Path $dll)) { throw 'Build the server Debug configuration first.' }
$start.Arguments = '"' + $dll + '"'
$start.WorkingDirectory = $root
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = 'Hidden'
$start.EnvironmentVariables['NDI_JOB_CONFIGURATOR_DATA_DIR'] = $data
$start.EnvironmentVariables['NDI_JOB_CONFIGURATOR_SERVICE_PORT'] = [string]$port
$start.EnvironmentVariables['NDI_JOB_CONFIGURATOR_LAN_ACCESS'] = '0'
$start.EnvironmentVariables['KILOVIEW_LAN_ACCESS'] = '0'
$process = [Diagnostics.Process]::Start($start)
$url = "http://127.0.0.1:$port/"
try {
    $state = $null
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    while ([datetime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Fixture server exited: $($process.ExitCode)" }
        try { $state = Invoke-RestMethod ($url + 'api/state') -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $state -or $state.integrationSchemaVersion -ne 1 -or -not $state.serverId -or -not $state.jobId -or -not $state.jobRevision) { throw 'Public HTTP state omitted its integration identity.' }
    if ($state.lastJob.jobName -ne 'Contract Fixture') { throw 'The fixture server did not use isolated state.' }
    $status = 0
    try { Invoke-WebRequest ($url + 'api/state') -UseBasicParsing -Headers @{Origin='https://untrusted.invalid'} | Out-Null }
    catch { $status = [int]$_.Exception.Response.StatusCode }
    if ($status -ne 403) { throw "Cross-origin management access was accepted: $status" }
    Write-Host 'PASS HTTP job identity and management origin boundary'
    if (-not $ResolumeRoot) {
        $suite = Get-Content (Join-Path $root 'suite.json') -Raw | ConvertFrom-Json
        $ResolumeRoot = [IO.Path]::GetFullPath((Join-Path $root $suite.resolume.path))
    }
    $reader = Join-Path $ResolumeRoot 'tests\ResolumeConfigurator.Tests\bin\Debug\net8.0-windows\ResolumeConfigurator.Tests.dll'
    if (-not (Test-Path $reader)) { throw 'Build the Resolume validation project first.' }
    $previous = $env:NDI_JOB_CONFIGURATOR_DATA_DIR
    try {
        $env:NDI_JOB_CONFIGURATOR_DATA_DIR = $data
        & dotnet $reader --verify-job-contract $url $state.serverId $state.jobId $state.jobRevision
        if ($LASTEXITCODE -ne 0) { throw 'Resolume rejected the real server HTTP contract.' }
    } finally { $env:NDI_JOB_CONFIGURATOR_DATA_DIR = $previous }
    $fixture.lastJob.startedUtc = '2026-09-06T12:00:01Z'
    $fixture | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding UTF8
    $replacement = Invoke-RestMethod ($url + 'api/state') -TimeoutSec 3
    if ($replacement.serverId -ne $state.serverId -or $replacement.jobId -eq $state.jobId -or $replacement.jobRevision -eq $state.jobRevision) { throw 'Same-name job replacement lost stable server/new job identity.' }
    Write-Host 'PASS HTTP same-name replacement changes job identity'
} finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
Write-Host "Isolated HTTP artifacts: $data"
