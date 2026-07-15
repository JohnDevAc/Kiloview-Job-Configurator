[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root 'artifacts'
$publish = Join-Path $artifactRoot 'KiloviewSetup'
$package = Join-Path $artifactRoot 'KiloviewSetup-Windows.zip'

if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

$arguments = @('publish', (Join-Path $root 'Kiloview.Setup.csproj'), '--configuration', $Configuration, '--output', $publish, '--configfile', (Join-Path $root 'NuGet.Config'))
if ($SelfContained) {
    $arguments += @('--runtime', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true')
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $root 'installer\Install-KiloviewSetup.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Uninstall-KiloviewSetup.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Install.cmd') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish

if (Test-Path $package) { Remove-Item -LiteralPath $package -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -CompressionLevel Optimal
Write-Host "Package created: $package"
