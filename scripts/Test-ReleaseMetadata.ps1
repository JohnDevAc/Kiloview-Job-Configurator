[CmdletBinding()]
param([string]$Root)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$propsPath = Join-Path $Root 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) {
    throw 'Directory.Build.props is required for shared release metadata.'
}

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]$props.Project.PropertyGroup.Version
$channel = [string]$props.Project.PropertyGroup.ReleaseChannel
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The shared Version is missing.' }
if ($channel -notin @('Main', 'Development')) {
    throw "ReleaseChannel must be Main or Development, not '$channel'."
}
if ($channel -eq 'Development' -and $version -notmatch '-dev\.') {
    throw "Development version '$version' must contain a -dev.N suffix."
}
if ($channel -eq 'Main' -and $version -match '-') {
    throw "Main version '$version' must not contain a prerelease suffix."
}

foreach ($project in @(
    (Join-Path $Root 'NDI.Job.Configurator.csproj'),
    (Join-Path $Root 'installer\NDI.Job.Configurator.Bootstrapper.csproj')
)) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    if ($projectXml.SelectNodes('//Version').Count -gt 0) {
        throw "$(Split-Path -Leaf $project) must inherit Version from Directory.Build.props."
    }
    if ($projectXml.SelectNodes('//Company | //Copyright | //Product').Count -gt 0) {
        throw "$(Split-Path -Leaf $project) contains duplicated shared product metadata."
    }
}

if ($env:GITHUB_REF_TYPE -eq 'tag' -and
    -not [string]::Equals($env:GITHUB_REF_NAME, "v$version", [StringComparison]::Ordinal)) {
    throw "Git tag '$($env:GITHUB_REF_NAME)' does not match shared version 'v$version'."
}

Write-Host "Release metadata valid: $version ($channel)"
