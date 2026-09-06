param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$suite = Get-Content -LiteralPath (Join-Path $root 'suite.json') -Raw | ConvertFrom-Json
$companion = (Resolve-Path -LiteralPath (Join-Path $root $suite.pcOnboarding.path)).Path
if (-not (Test-Path -LiteralPath (Join-Path $companion '.git'))) { throw 'The companion must remain a separate Git checkout.' }
& dotnet build (Join-Path $root 'NDI.Job.Configurator.csproj') --configuration Debug
if ($LASTEXITCODE -ne 0) { throw 'Build the server before running the outcome contract tests.' }
& dotnet run --project (Join-Path $PSScriptRoot 'Interop\Interop.csproj') "-p:CompanionRoot=$companion"
if ($LASTEXITCODE -ne 0) { throw 'Cross-repository onboarding validation failed.' }
