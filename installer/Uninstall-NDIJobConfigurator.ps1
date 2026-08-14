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

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$installRoot = Join-Path $localAppData 'Programs\NDI Job Configurator'
$legacyInstallRoot = Join-Path $localAppData 'Programs\Kiloview Setup'
$startup = [Environment]::GetFolderPath('Startup')
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
$taskNames = @('NDI Job Configurator Service', 'Kiloview Job Configurator Service')
$firewallRules = @('NDI Job Configurator LAN', 'NDI Job Configurator NDI', 'Kiloview Job Configurator LAN', 'Kiloview Job Configurator NDI')
$knownExecutables = @(
    (Join-Path $installRoot 'NDIJobConfigurator.exe'),
    (Join-Path $legacyInstallRoot 'KiloviewSetup.exe')
) | ForEach-Object { [IO.Path]::GetFullPath($_) }

$runningProcesses = @(Get-Process NDIJobConfigurator, KiloviewSetup -ErrorAction SilentlyContinue | Where-Object {
    try { $knownExecutables -contains [IO.Path]::GetFullPath($_.Path) } catch { $false }
})
foreach ($runningProcess in $runningProcesses) { $runningProcess | Stop-Process -Force }
foreach ($runningProcess in $runningProcesses) {
    try {
        if (-not $runningProcess.WaitForExit(15000)) {
            throw "NDI Job Configurator process $($runningProcess.Id) did not stop within 15 seconds."
        }
    }
    finally { $runningProcess.Dispose() }
}

if ($PSCmdlet.ShouldProcess($installRoot, 'Remove NDI Job Configurator application files')) {
    foreach ($name in $taskNames) {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
        }
    }
    foreach ($name in $firewallRules) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    }
    @(
        (Join-Path $startup 'NDI Job Configurator Service.lnk'),
        (Join-Path $startup 'Kiloview Job Configurator Service.lnk'),
        (Join-Path $startup 'Kiloview Setup Service.lnk'),
        (Join-Path $desktop 'NDI Job Configurator.lnk'),
        (Join-Path $desktop 'Kiloview Job Configurator.lnk'),
        (Join-Path $desktop 'Kiloview Job Configurator.url'),
        (Join-Path $desktop 'Kiloview Setup.url'),
        (Join-Path $programs 'NDI Job Configurator'),
        (Join-Path $programs 'Kiloview Job Configurator'),
        (Join-Path $programs 'Kiloview Setup')
    ) | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $profileRoot = Split-Path (Split-Path $localAppData -Parent) -Parent
    $profileEntry = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' | Where-Object {
        $profilePath = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty $_.PSPath).ProfileImagePath)
        [string]::Equals([IO.Path]::GetFullPath($profilePath), [IO.Path]::GetFullPath($profileRoot), [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1
    if (-not $profileEntry) { throw "Windows profile SID could not be resolved for $profileRoot." }
    $interactiveSid = $profileEntry.PSChildName
    $uninstallRoot = "Registry::HKEY_USERS\$interactiveSid\Software\Microsoft\Windows\CurrentVersion\Uninstall"
    Remove-Item -LiteralPath (Join-Path $uninstallRoot 'NDIJobConfigurator') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $uninstallRoot 'KiloviewSetup') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $legacyInstallRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'NDI Job Configurator was removed. Monitoring data in LocalAppData\NDI Job Configurator was preserved.'
