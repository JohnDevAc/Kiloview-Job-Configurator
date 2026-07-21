[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Kiloview Setup'
$startup = [Environment]::GetFolderPath('Startup')
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
$startupLinks = @(
    (Join-Path $startup 'Kiloview Job Configurator Service.lnk'),
    (Join-Path $startup 'Kiloview Setup Service.lnk')
)
$desktopLinks = @(
    (Join-Path $desktop 'Kiloview Job Configurator.url'),
    (Join-Path $desktop 'Kiloview Setup.url')
)
$startMenus = @(
    (Join-Path $programs 'Kiloview Job Configurator'),
    (Join-Path $programs 'Kiloview Setup')
)

Get-Process KiloviewSetup -ErrorAction SilentlyContinue | Stop-Process -Force
if ($PSCmdlet.ShouldProcess($installRoot, 'Remove Kiloview Job Configurator application files')) {
    $startupLinks | Remove-Item -Force -ErrorAction SilentlyContinue
    $desktopLinks | Remove-Item -Force -ErrorAction SilentlyContinue
    $startMenus | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KiloviewSetup' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Kiloview Job Configurator was removed. Monitoring data in LocalAppData\Kiloview Setup was preserved.'
