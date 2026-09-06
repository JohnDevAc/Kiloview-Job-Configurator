$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\installer\PcAgentPackage.ps1')
foreach ($case in @(
    @('0.7.1', '0.7.0-dev.1', 1), @('0.7.0', '0.7.0-dev.9', 1),
    @('0.7.0-dev.10', '0.7.0-dev.9', 1), @('0.7.0-dev.1', '0.7.0-dev.2', -1),
    @('0.7.0+abc', '0.7.0+def', 0)
)) {
    if ([Math]::Sign((Compare-PcAgentVersion $case[0] $case[1])) -ne $case[2]) { throw "Version comparison failed: $case" }
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ndi-package-test-' + [Guid]::NewGuid().ToString('N'))
$component = Join-Path $testRoot 'pc-onboarding'
New-Item -ItemType Directory -Path (Join-Path $component 'Agent') -Force | Out-Null
$files = foreach ($name in @('NDI Configurator PC Agent Setup.exe', 'Agent\NDI Configurator PC Agent.exe', 'LICENSE.md')) {
    $path = Join-Path $component $name
    Set-Content -LiteralPath $path -Value 'Package validation fixture'
    @{ path = $name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
$manifest = @{ schemaVersion=1; localCommandSchema=1; repository='JohnDevAc/Kiloview-PC-Onboarding'; version='0.7.0'; files=@($files) }
$manifestPath = Join-Path $testRoot 'pc-onboarding-manifest.json'
function Save-Manifest { $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath }
Save-Manifest
[void](Test-PcAgentPackage $testRoot)
$manifest.files = @()
Save-Manifest
try { [void](Test-PcAgentPackage $testRoot); throw 'EMPTY_MANIFEST_ACCEPTED' } catch { if ($_.Exception.Message -eq 'EMPTY_MANIFEST_ACCEPTED') { throw } }
$manifest.files = @($files)
Save-Manifest
Add-Content -LiteralPath (Join-Path $component 'NDI Configurator PC Agent Setup.exe') -Value 'corruption'
try { [void](Test-PcAgentPackage $testRoot); throw 'CORRUPT_PACKAGE_ACCEPTED' } catch { if ($_.Exception.Message -eq 'CORRUPT_PACKAGE_ACCEPTED') { throw } }
Write-Output 'PASS Independent version ordering, complete package manifest, and payload corruption checks'
