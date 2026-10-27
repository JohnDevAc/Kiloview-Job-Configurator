[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SetupExe,
    [string]$Configuration = 'Release',
    [string]$CompanionRoot,
    [ValidateNotNullOrEmpty()][string]$CompanionPackage,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$CompanionPackageSha256
)

$ErrorActionPreference = 'Stop'
if ($PSBoundParameters.ContainsKey('CompanionPackage') -ne $PSBoundParameters.ContainsKey('CompanionPackageSha256')) {
    throw 'A prebuilt companion package requires both its path and expected SHA256.'
}
if ($CompanionPackage) {
    $CompanionPackage = (Resolve-Path -LiteralPath $CompanionPackage).Path
    if ([IO.Path]::GetExtension($CompanionPackage) -ne '.zip' -or
        (Get-FileHash -LiteralPath $CompanionPackage -Algorithm SHA256).Hash -ne $CompanionPackageSha256) {
        throw 'The prebuilt companion ZIP does not match its expected SHA256.'
    }
}
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'installer\PcAgentPackage.ps1')
. (Join-Path $PSScriptRoot 'CompanionArchive.ps1')
$suite = Get-Content -LiteralPath (Join-Path $root 'suite.json') -Raw | ConvertFrom-Json
if (-not $CompanionRoot) { $CompanionRoot = Join-Path $root $suite.pcOnboarding.path }
$CompanionRoot = (Resolve-Path -LiteralPath $CompanionRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $CompanionRoot '.git'))) { throw 'The companion must be a separate Git checkout.' }
[xml]$companionProps = Get-Content -LiteralPath (Join-Path $CompanionRoot 'Directory.Build.props') -Raw
$companionVersion = [string]$companionProps.Project.PropertyGroup.Version
if ([version]($companionVersion.Split('-')[0]) -lt [version]$suite.pcOnboarding.minimumVersion) {
    throw "PC Agent $($suite.pcOnboarding.minimumVersion) or later is required for local server onboarding."
}
if ($CompanionPackage) {
    Test-CompanionArchive $CompanionPackage $CompanionPackageSha256 (Join-Path $CompanionRoot 'artifacts\win-x64')
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
Copy-Item -LiteralPath (Join-Path $root 'JOB-AGENT-LIFECYCLE.md') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Select-InstallOptions.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\PcAgentPackage.ps1') -Destination $publish
foreach ($name in @('JobLifecycle.ps1','JobLifecycle.PathSafety.cs','JobLifecycle.Windows.ps1','JobLifecycle.Package.ps1','Invoke-NDIJobConfiguratorLifecycle.ps1','toolbox-lifecycle.json')) {
    Copy-Item -LiteralPath (Join-Path $root "installer\$name") -Destination $publish
}

# The companion remains an independently built and released application. Bundle
# its complete package as an optional installer component, never compile its source.
if (-not $CompanionPackage) {
    & (Join-Path $CompanionRoot 'scripts\Publish.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'PC Agent package build failed.' }
    $CompanionPackage = Join-Path $CompanionRoot 'artifacts\NDI-Configurator-PC-Agent-win-x64.zip'
}
$companionArchiveHash = (Get-FileHash -LiteralPath $CompanionPackage -Algorithm SHA256).Hash
if ($CompanionPackageSha256 -and $companionArchiveHash -ne $CompanionPackageSha256) {
    throw 'The prebuilt companion ZIP changed while building the server.'
}
Test-CompanionArchive $CompanionPackage $companionArchiveHash (Join-Path $CompanionRoot 'artifacts\win-x64')
$componentRoot = Join-Path $publish 'pc-onboarding'
Expand-Archive -LiteralPath $CompanionPackage -DestinationPath $componentRoot
foreach ($binary in @('NDI Configurator PC Agent Setup.exe','Agent\NDI Configurator PC Agent.exe')) {
    $info = (Get-Item -LiteralPath (Join-Path $componentRoot $binary)).VersionInfo
    if ($info.ProductName -ne 'NDI Configurator PC Agent' -or
        (Compare-PcAgentVersion $info.ProductVersion $companionVersion) -ne 0) {
        throw 'The companion package product/version does not match its separate source checkout.'
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $componentRoot 'LICENSE.md'))) { throw 'The complete companion package licence is required.' }
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
    sourceArchiveSha256 = $companionArchiveHash
    sourceArchiveBytes = (Get-Item -LiteralPath $CompanionPackage).Length
    localCommandSchema = $suite.pcOnboarding.localCommandSchema
    files = @(Get-ChildItem -LiteralPath $componentRoot -Recurse -File | ForEach-Object {
        @{ path = $_.FullName.Substring($componentRoot.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publish 'pc-onboarding-manifest.json') -Encoding UTF8

if (Test-Path $package) { Remove-Item -LiteralPath $package -Force }
[xml]$serverProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
@{
    schemaVersion = 1; productId = 'job'; version = [string]$serverProps.Project.PropertyGroup.Version
    channel = [string]$serverProps.Project.PropertyGroup.ReleaseChannel
    files = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
        -not $_.FullName.StartsWith($componentRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        $_.Name -notin @('pc-onboarding-manifest.json','job-package-manifest.json')
    } | ForEach-Object { @{path=$_.FullName.Substring($publish.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $publish 'job-package-manifest.json') -Encoding UTF8
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
