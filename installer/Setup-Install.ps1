[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$payload = Join-Path $PSScriptRoot 'KiloviewSetup-Payload.zip'
if (-not (Test-Path -LiteralPath $payload)) { throw 'The installer payload is missing.' }

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$extract = Join-Path $temporaryRoot ("KiloviewSetup-" + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $extract -Force | Out-Null
    Expand-Archive -LiteralPath $payload -DestinationPath $extract -Force
    & (Join-Path $extract 'Install-KiloviewSetup.ps1') -Source $extract
}
finally {
    $resolved = [IO.Path]::GetFullPath($extract)
    if ($resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
