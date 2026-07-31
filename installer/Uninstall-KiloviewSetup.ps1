[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $elevationArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $elevatedUninstaller = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $elevationArguments -Wait -PassThru
    exit $elevatedUninstaller.ExitCode
}

$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Kiloview Setup'
$startup = [Environment]::GetFolderPath('Startup')
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
$startupLinks = @(
    (Join-Path $startup 'Kiloview Job Configurator Service.lnk'),
    (Join-Path $startup 'Kiloview Setup Service.lnk')
)
$desktopLinks = @(
    (Join-Path $desktop 'Kiloview Job Configurator.lnk'),
    (Join-Path $desktop 'Kiloview Job Configurator.url'),
    (Join-Path $desktop 'Kiloview Setup.url')
)
$startMenus = @(
    (Join-Path $programs 'Kiloview Job Configurator'),
    (Join-Path $programs 'Kiloview Setup')
)
$scheduledTaskName = 'Kiloview Job Configurator Service'
$firewallRuleName = 'Kiloview Job Configurator LAN'
$ndiFirewallRuleName = 'Kiloview Job Configurator NDI'
$installedExe = Join-Path $installRoot 'KiloviewSetup.exe'

$runningProcesses = @(Get-Process KiloviewSetup -ErrorAction SilentlyContinue | Where-Object {
    try {
        [string]::Equals(
            [IO.Path]::GetFullPath($_.Path),
            [IO.Path]::GetFullPath($installedExe),
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch { $false }
})
foreach ($runningProcess in $runningProcesses) {
    $runningProcess | Stop-Process -Force
}
foreach ($runningProcess in $runningProcesses) {
    try {
        if (-not $runningProcess.WaitForExit(15000)) {
            throw "Kiloview Job Configurator process $($runningProcess.Id) did not stop within 15 seconds."
        }
    }
    finally {
        $runningProcess.Dispose()
    }
}
if ($PSCmdlet.ShouldProcess($installRoot, 'Remove Kiloview Job Configurator application files')) {
    $scheduledTask = Get-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
    if ($scheduledTask) {
        Stop-ScheduledTask -TaskName $scheduledTaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $scheduledTaskName -Confirm:$false
    }
    Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Get-NetFirewallRule -DisplayName $ndiFirewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $startupLinks | Remove-Item -Force -ErrorAction SilentlyContinue
    $desktopLinks | Remove-Item -Force -ErrorAction SilentlyContinue
    $startMenus | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KiloviewSetup' -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Kiloview Job Configurator was removed. Monitoring data in LocalAppData\Kiloview Setup was preserved.'
