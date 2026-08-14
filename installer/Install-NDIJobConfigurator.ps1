[CmdletBinding()]
param([string]$Source = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $resolvedSource = [IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $elevationArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Source `"$resolvedSource`""
    $elevatedInstaller = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $elevationArguments -Wait -PassThru
    exit $elevatedInstaller.ExitCode
}

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$installRoot = Join-Path $localAppData 'Programs\NDI Job Configurator'
$legacyInstallRoot = Join-Path $localAppData 'Programs\Kiloview Setup'
$dataRoot = Join-Path $localAppData 'NDI Job Configurator'
$legacyDataRoot = Join-Path $localAppData 'Kiloview Setup'
$startup = [Environment]::GetFolderPath('Startup')
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
$startMenu = Join-Path $programs 'NDI Job Configurator'
$taskName = 'NDI Job Configurator Service'
$legacyTaskName = 'Kiloview Job Configurator Service'
$firewallRuleName = 'NDI Job Configurator LAN'
$ndiFirewallRuleName = 'NDI Job Configurator NDI'
$legacyFirewallRules = @('Kiloview Job Configurator LAN', 'Kiloview Job Configurator NDI')
$installedExe = Join-Path $installRoot 'NDIJobConfigurator.exe'
$legacyInstalledExe = Join-Path $legacyInstallRoot 'KiloviewSetup.exe'
$sourceExe = Join-Path $Source 'NDIJobConfigurator.exe'
if (-not (Test-Path $sourceExe)) { throw "NDIJobConfigurator.exe was not found in $Source. Run scripts\Publish.ps1 first." }

$runtimeConfig = Join-Path $Source 'NDIJobConfigurator.runtimeconfig.json'
if (Test-Path $runtimeConfig) {
    $runtimeSettings = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
    $needsSharedRuntime = $null -ne $runtimeSettings.runtimeOptions.framework -or $null -ne $runtimeSettings.runtimeOptions.frameworks
    if ($needsSharedRuntime) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if (-not ($runtimes -match 'Microsoft\.AspNetCore\.App 8\.')) {
            throw 'The .NET 8 ASP.NET Core Runtime is required. Use NDI-Job-Configurator.exe or install the .NET 8 ASP.NET Core Runtime.'
        }
    }
}

foreach ($name in @($taskName, $legacyTaskName)) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    }
}

$knownExecutables = @($installedExe, $legacyInstalledExe) | ForEach-Object { [IO.Path]::GetFullPath($_) }
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

$expectedInstallRoot = [IO.Path]::GetFullPath((Join-Path $localAppData 'Programs\NDI Job Configurator')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$resolvedInstallRoot = [IO.Path]::GetFullPath($installRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$expectedLegacyInstallRoot = [IO.Path]::GetFullPath((Join-Path $localAppData 'Programs\Kiloview Setup')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$resolvedLegacyInstallRoot = [IO.Path]::GetFullPath($legacyInstallRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not [string]::Equals($resolvedInstallRoot, $expectedInstallRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($resolvedLegacyInstallRoot, $expectedLegacyInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to modify an unexpected installation directory.'
}
$resolvedSourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($resolvedSourceRoot.StartsWith($resolvedInstallRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($resolvedSourceRoot, $resolvedInstallRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $resolvedSourceRoot.StartsWith($resolvedLegacyInstallRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($resolvedSourceRoot, $resolvedLegacyInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Run the installer from its downloaded or extracted package, not from inside an existing installation directory.'
}

if (-not (Test-Path -LiteralPath $dataRoot) -and (Test-Path -LiteralPath $legacyDataRoot)) {
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $legacyDataRoot -Force | Copy-Item -Destination $dataRoot -Recurse -Force
}

if (Test-Path -LiteralPath $resolvedInstallRoot) {
    Get-ChildItem -LiteralPath $resolvedInstallRoot -Force | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedInstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
Get-ChildItem -LiteralPath $Source -File | Where-Object Extension -in '.exe','.dll','.json','.pdb','.ico','.md','.txt' | Copy-Item -Destination $installRoot -Force
if (Test-Path (Join-Path $Source 'wwwroot')) { Copy-Item -LiteralPath (Join-Path $Source 'wwwroot') -Destination $installRoot -Recurse -Force }
if (Test-Path (Join-Path $Source 'THIRD-PARTY-NOTICES')) { Copy-Item -LiteralPath (Join-Path $Source 'THIRD-PARTY-NOTICES') -Destination $installRoot -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $Source 'Uninstall-NDIJobConfigurator.ps1') -Destination $installRoot -Force
Copy-Item -LiteralPath (Join-Path $Source 'Launch-NDIJobConfigurator.ps1') -Destination $installRoot -Force

foreach ($name in @($taskName, $legacyTaskName)) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
    }
}
if (Test-Path -LiteralPath $resolvedLegacyInstallRoot) {
    Remove-Item -LiteralPath $resolvedLegacyInstallRoot -Recurse -Force
}

$icon = Join-Path $installRoot 'NDIJobConfigurator.ico'
$launcher = Join-Path $installRoot 'Launch-NDIJobConfigurator.ps1'
$shell = New-Object -ComObject WScript.Shell
$obsoleteItems = @(
    (Join-Path $startup 'Kiloview Setup Service.lnk'),
    (Join-Path $startup 'Kiloview Job Configurator Service.lnk'),
    (Join-Path $desktop 'Kiloview Setup.url'),
    (Join-Path $desktop 'Kiloview Job Configurator.url'),
    (Join-Path $desktop 'Kiloview Job Configurator.lnk'),
    (Join-Path $desktop 'NDI Job Configurator.url'),
    (Join-Path $programs 'Kiloview Setup'),
    (Join-Path $programs 'Kiloview Job Configurator')
)
$obsoleteItems | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$profileRoot = Split-Path (Split-Path $localAppData -Parent) -Parent
$profileEntry = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' | Where-Object {
    $profilePath = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty $_.PSPath).ProfileImagePath)
    [string]::Equals([IO.Path]::GetFullPath($profilePath), [IO.Path]::GetFullPath($profileRoot), [StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
if (-not $profileEntry) { throw "Windows profile SID could not be resolved for $profileRoot." }
$interactiveSid = $profileEntry.PSChildName
$currentUser = ([Security.Principal.SecurityIdentifier]$interactiveSid).Translate([Security.Principal.NTAccount]).Value
$taskAction = New-ScheduledTaskAction -Execute $installedExe -Argument '--lan' -WorkingDirectory $installRoot
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Settings $taskSettings -Description 'Elevated NDI Job Configurator private LAN web service' -Force | Out-Null

foreach ($name in @($firewallRuleName, $ndiFirewallRuleName) + $legacyFirewallRules) {
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}
New-NetFirewallRule -DisplayName $firewallRuleName -Description 'Allows NDI Job Configurator web access from the local subnet on trusted Windows network profiles.' -Direction Inbound -Action Allow -Enabled True -Profile Domain,Private -Program $installedExe -Protocol TCP -LocalPort 8091 -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block | Out-Null
New-NetFirewallRule -DisplayName $ndiFirewallRuleName -Description 'Allows NDI Job Configurator to receive NDI multicast previews from the trusted local subnet.' -Direction Inbound -Action Allow -Enabled True -Profile Domain,Private -Program $installedExe -Protocol UDP -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block | Out-Null

$shortcutArguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`""
foreach ($shortcutPath in @((Join-Path $desktop 'NDI Job Configurator.lnk'), (Join-Path $startMenu 'NDI Job Configurator.lnk'))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $PSHOME 'powershell.exe'
    $shortcut.Arguments = $shortcutArguments
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.WindowStyle = 0
    $shortcut.Description = 'Open NDI Job Configurator with its elevated local service'
    $shortcut.IconLocation = "$icon,0"
    $shortcut.Save()
}

$uninstallRoot = "Registry::HKEY_USERS\$interactiveSid\Software\Microsoft\Windows\CurrentVersion\Uninstall"
$uninstallKey = Join-Path $uninstallRoot 'NDIJobConfigurator'
Remove-Item -LiteralPath (Join-Path $uninstallRoot 'KiloviewSetup') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $uninstallKey -Force | Out-Null
$displayVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.ProductVersion
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'NDI Job Configurator' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $displayVersion -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'John Lightfoot' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value $icon -PropertyType String -Force | Out-Null
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall-NDIJobConfigurator.ps1')`""
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Start-ScheduledTask -TaskName $taskName
$healthy = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8091/api/health' -TimeoutSec 1
        if ($health.status -eq 'ok') { $healthy = $true; break }
    }
    catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $healthy) { throw 'NDI Job Configurator was installed but did not start successfully on port 8091.' }
Start-Process 'http://localhost:8091'
Write-Host "NDI Job Configurator installed for the current user at $installRoot"
Write-Host 'Existing application state and saved KiloLink credentials were retained.'
Write-Host 'LAN access is enabled on TCP 8091 and NDI multicast preview reception is enabled over UDP on trusted local networks.'
