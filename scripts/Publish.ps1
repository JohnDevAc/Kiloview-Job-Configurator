[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SetupExe,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root 'artifacts'
$publish = Join-Path $artifactRoot 'KiloviewJobConfigurator'
$package = Join-Path $artifactRoot 'Kiloview-Job-Configurator-Windows.zip'
$setup = Join-Path $artifactRoot 'Kiloview-Job-Configurator.exe'
$legacySetups = @(
    (Join-Path $artifactRoot 'KiloviewSetup-Setup.exe'),
    (Join-Path $artifactRoot 'Kiloview Job Setup Manager.exe')
)

if ($SetupExe) { $SelfContained = $true }

if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

$arguments = @('publish', (Join-Path $root 'Kiloview.Setup.csproj'), '--configuration', $Configuration, '--output', $publish, '--configfile', (Join-Path $root 'NuGet.Config'))
if ($SelfContained) {
    $arguments += @(
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '--source', 'https://api.nuget.org/v3/index.json'
    )
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $root 'installer\Install-KiloviewSetup.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Uninstall-KiloviewSetup.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Launch-KiloviewJobConfigurator.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Install.cmd') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'assets\KiloviewSetup.ico') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish

if (Test-Path $package) { Remove-Item -LiteralPath $package -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -CompressionLevel Optimal
Write-Host "Package created: $package"

if ($SetupExe) {
    $legacySetups | Remove-Item -Force -ErrorAction SilentlyContinue
    $bootstrapperPublish = Join-Path $artifactRoot 'bootstrapper'
    if (Test-Path $bootstrapperPublish) { Remove-Item -LiteralPath $bootstrapperPublish -Recurse -Force }
    $bootstrapperArguments = @(
        'publish', (Join-Path $root 'installer\Kiloview.Setup.Bootstrapper.csproj'),
        '--configuration', $Configuration,
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--output', $bootstrapperPublish,
        '--configfile', (Join-Path $root 'NuGet.Config'),
        '--source', 'https://api.nuget.org/v3/index.json',
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    & dotnet @bootstrapperArguments
    if ($LASTEXITCODE -ne 0) { throw 'bootstrapper publish failed.' }

    Copy-Item -LiteralPath (Join-Path $bootstrapperPublish 'Kiloview Job Configurator.exe') -Destination $setup -Force
    Remove-Item -LiteralPath $bootstrapperPublish -Recurse -Force
    Write-Host "Branded installer created: $setup"
}
