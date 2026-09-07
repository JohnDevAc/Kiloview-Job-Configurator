[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SetupExe,
    [string]$Configuration = 'Release',
    [string]$CompanionRoot
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$suite = Get-Content -LiteralPath (Join-Path $root 'suite.json') -Raw | ConvertFrom-Json
if (-not $CompanionRoot) { $CompanionRoot = Join-Path $root $suite.pcOnboarding.path }
$CompanionRoot = (Resolve-Path -LiteralPath $CompanionRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $CompanionRoot '.git'))) { throw 'The companion must be a separate Git checkout.' }
[xml]$companionProps = Get-Content -LiteralPath (Join-Path $CompanionRoot 'Directory.Build.props') -Raw
$companionVersion = [string]$companionProps.Project.PropertyGroup.Version
if ([version]($companionVersion.Split('-')[0]) -lt [version]$suite.pcOnboarding.minimumVersion) {
    throw "PC Agent $($suite.pcOnboarding.minimumVersion) or later is required for local server onboarding."
}
$artifactRoot = Join-Path $root 'artifacts'
$publish = Join-Path $artifactRoot 'NDIJobConfigurator'
$package = Join-Path $artifactRoot 'NDI-Job-Configurator-Windows.zip'
$setup = Join-Path $artifactRoot 'NDI-Job-Configurator.exe'
$legacySetupAlias = Join-Path $artifactRoot 'Kiloview-Job-Configurator.exe'
$legacySetups = @(
    (Join-Path $artifactRoot 'KiloviewSetup-Setup.exe'),
    (Join-Path $artifactRoot 'Kiloview Job Setup Manager.exe')
)

if ($SetupExe) { $SelfContained = $true }

& (Join-Path $PSScriptRoot 'Test-ReleaseMetadata.ps1') -Root $root

foreach ($leaf in @('NDIJobConfigurator', 'bootstrapper')) {
    $target = [IO.Path]::GetFullPath((Join-Path $artifactRoot $leaf))
    if (-not $target.StartsWith([IO.Path]::GetFullPath($artifactRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe publish cleanup path.'
    }
    if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target -Force).LinkType) { throw 'Publish output cannot be a filesystem link.' }
}

if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

$arguments = @('publish', (Join-Path $root 'NDI.Job.Configurator.csproj'), '--configuration', $Configuration, '--output', $publish, '--configfile', (Join-Path $root 'NuGet.Config'))
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

Copy-Item -LiteralPath (Join-Path $root 'installer\Install-NDIJobConfigurator.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Uninstall-NDIJobConfigurator.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Launch-NDIJobConfigurator.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Install.cmd') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'wwwroot\NDIJobConfigurator.ico') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'INTEROPERABILITY.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'PC-ONBOARDING-CONTRACT.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'ONBOARDING-DIAGNOSTICS.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Select-InstallOptions.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\PcAgentPackage.ps1') -Destination $publish

# The companion remains an independently built and released application. Bundle
# its complete package as an optional installer component, never compile its source.
& (Join-Path $CompanionRoot 'scripts\Publish.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'PC Agent package build failed.' }
$companionPackage = Join-Path $CompanionRoot 'artifacts\NDI-Configurator-PC-Agent-win-x64.zip'
$componentRoot = Join-Path $publish 'pc-onboarding'
Expand-Archive -LiteralPath $companionPackage -DestinationPath $componentRoot
$companionCommit = (& git -C $CompanionRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not determine the companion source revision.' }
$companionDirty = [bool](& git -C $CompanionRoot status --porcelain --untracked-files=normal)
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $companionDirty) { throw 'Release builds require a clean companion checkout.' }
@{
    schemaVersion = 1
    repository = $suite.pcOnboarding.repository
    version = $companionVersion
    commit = $companionCommit
    workingTreeChanges = $companionDirty
    localCommandSchema = $suite.pcOnboarding.localCommandSchema
    files = @(Get-ChildItem -LiteralPath $componentRoot -Recurse -File | ForEach-Object {
        @{ path = $_.FullName.Substring($componentRoot.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publish 'pc-onboarding-manifest.json') -Encoding UTF8

if (Test-Path $package) { Remove-Item -LiteralPath $package -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -CompressionLevel Optimal
Write-Host "Package created: $package"

if ($SetupExe) {
    $legacySetups | Remove-Item -Force -ErrorAction SilentlyContinue
    $bootstrapperPublish = Join-Path $artifactRoot 'bootstrapper'
    if (Test-Path $bootstrapperPublish) { Remove-Item -LiteralPath $bootstrapperPublish -Recurse -Force }
    $bootstrapperArguments = @(
        'publish', (Join-Path $root 'installer\NDI.Job.Configurator.Bootstrapper.csproj'),
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

    Copy-Item -LiteralPath (Join-Path $bootstrapperPublish 'NDI Job Configurator.exe') -Destination $setup -Force
    Copy-Item -LiteralPath $setup -Destination $legacySetupAlias -Force
    Remove-Item -LiteralPath $bootstrapperPublish -Recurse -Force
    Write-Host "Branded installer created: $setup"
    Write-Host "Legacy updater alias created: $legacySetupAlias"
}
