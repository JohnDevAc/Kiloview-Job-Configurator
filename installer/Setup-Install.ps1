[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$payload = Join-Path $PSScriptRoot 'KiloviewSetup-Payload.zip'
$log = Join-Path ([IO.Path]::GetTempPath()) 'KiloviewSetup-Installer.log'
function Write-InstallerLog([string]$Message) {
    Add-Content -LiteralPath $log -Value "$(Get-Date -Format o) $Message" -Encoding UTF8
}

Write-InstallerLog "Installer started from $PSScriptRoot"
if (-not (Test-Path -LiteralPath $payload)) {
    Write-InstallerLog "Payload missing: $payload"
    throw 'The installer payload is missing.'
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$extract = Join-Path $temporaryRoot ("KiloviewSetup-" + [Guid]::NewGuid().ToString('N'))
try {
    Write-InstallerLog "Extracting payload to $extract"
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Expand-Archive -LiteralPath $payload -DestinationPath $extract -Force
    Write-InstallerLog 'Payload extracted; starting per-user installer'
    & (Join-Path $extract 'Install-KiloviewSetup.ps1') -Source $extract
    Write-InstallerLog 'Installation completed successfully'
}
catch {
    Write-InstallerLog "Installation failed: $($_ | Out-String)"
    throw
}
finally {
    $resolved = [IO.Path]::GetFullPath($extract)
    if ($resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
