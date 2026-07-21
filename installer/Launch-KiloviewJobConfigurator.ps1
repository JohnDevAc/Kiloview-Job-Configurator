[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskName = 'Kiloview Job Configurator Service'
$applicationUrl = 'http://localhost:8091'
$exe = Join-Path $PSScriptRoot 'KiloviewSetup.exe'

function Test-KiloviewService {
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8091/api/health' -TimeoutSec 1
        return $health.status -eq 'ok'
    }
    catch { return $false }
}

if (-not (Test-KiloviewService)) {
    try {
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    }
    catch {
        if (-not (Test-Path -LiteralPath $exe)) { throw 'Kiloview Job Configurator is not installed correctly.' }
        Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -ArgumentList '--open-browser' -Verb RunAs
        exit 0
    }

    $healthy = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (Test-KiloviewService) {
            $healthy = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $healthy) { throw 'The elevated Kiloview Job Configurator service did not start.' }
}

Start-Process $applicationUrl
