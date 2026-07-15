[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Kiloview Setup'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Kiloview Setup Service.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Kiloview Setup.url'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Kiloview Setup'

Get-Process KiloviewSetup -ErrorAction SilentlyContinue | Stop-Process -Force
if ($PSCmdlet.ShouldProcess($installRoot, 'Remove Kiloview Setup application files')) {
    Remove-Item -LiteralPath $startupLink -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $desktopLink -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $startMenu -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KiloviewSetup' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Kiloview Setup was removed. Monitoring data in LocalAppData\Kiloview Setup was preserved.'
