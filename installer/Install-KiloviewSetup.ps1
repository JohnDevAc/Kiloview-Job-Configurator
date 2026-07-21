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

# Keep these internal paths stable so existing installations and saved jobs upgrade in place.
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Kiloview Setup'
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kiloview Setup'
$startup = [Environment]::GetFolderPath('Startup')
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
$startMenu = Join-Path $programs 'Kiloview Job Configurator'
$legacyStartMenu = Join-Path $programs 'Kiloview Setup'
$scheduledTaskName = 'Kiloview Job Configurator Service'

$sourceExe = Join-Path $Source 'KiloviewSetup.exe'
if (-not (Test-Path $sourceExe)) { throw "KiloviewSetup.exe was not found in $Source. Run scripts\Publish.ps1 first." }

$runtimeConfig = Join-Path $Source 'KiloviewSetup.runtimeconfig.json'
if (Test-Path $runtimeConfig) {
    $runtimeSettings = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
    $needsSharedRuntime = $null -ne $runtimeSettings.runtimeOptions.framework -or $null -ne $runtimeSettings.runtimeOptions.frameworks
    if ($needsSharedRuntime) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if (-not ($runtimes -match 'Microsoft\.AspNetCore\.App 8\.')) {
            throw 'The .NET 8 ASP.NET Core Runtime is required. Use Kiloview-Job-Configurator.exe or install the .NET 8 ASP.NET Core Runtime.'
        }
    }
}

Get-Process KiloviewSetup -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
Get-ChildItem -LiteralPath $Source -File | Where-Object Extension -in '.exe','.dll','.json','.pdb','.ico','.md','.txt' | Copy-Item -Destination $installRoot -Force
if (Test-Path (Join-Path $Source 'wwwroot')) { Copy-Item -LiteralPath (Join-Path $Source 'wwwroot') -Destination $installRoot -Recurse -Force }
if (Test-Path (Join-Path $Source 'THIRD-PARTY-NOTICES')) { Copy-Item -LiteralPath (Join-Path $Source 'THIRD-PARTY-NOTICES') -Destination $installRoot -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $Source 'Uninstall-KiloviewSetup.ps1') -Destination $installRoot -Force
Copy-Item -LiteralPath (Join-Path $Source 'Launch-KiloviewJobConfigurator.ps1') -Destination $installRoot -Force

$exe = Join-Path $installRoot 'KiloviewSetup.exe'
$icon = Join-Path $installRoot 'KiloviewSetup.ico'
$launcher = Join-Path $installRoot 'Launch-KiloviewJobConfigurator.ps1'
$shell = New-Object -ComObject WScript.Shell
$legacyStartupLink = Join-Path $startup 'Kiloview Setup Service.lnk'
$currentStartupLink = Join-Path $startup 'Kiloview Job Configurator Service.lnk'
$legacyDesktopLink = Join-Path $desktop 'Kiloview Setup.url'
Remove-Item -LiteralPath $legacyStartupLink -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $currentStartupLink -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyDesktopLink -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $desktop 'Kiloview Job Configurator.url') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $legacyStartMenu 'Kiloview Setup.url') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $startMenu 'Kiloview Job Configurator.url') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $legacyStartMenu -Force -ErrorAction SilentlyContinue

$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$taskAction = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $installRoot
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $scheduledTaskName -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Settings $taskSettings -Description 'Elevated Kiloview Job Configurator local web service' -Force | Out-Null

$shortcutArguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launcher`""
foreach ($shortcutPath in @((Join-Path $desktop 'Kiloview Job Configurator.lnk'), (Join-Path $startMenu 'Kiloview Job Configurator.lnk'))) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $PSHOME 'powershell.exe'
    $shortcut.Arguments = $shortcutArguments
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.WindowStyle = 0
    $shortcut.Description = 'Open Kiloview Job Configurator with its elevated local service'
    $shortcut.IconLocation = "$icon,0"
    $shortcut.Save()
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KiloviewSetup'
New-Item -Path $uninstallKey -Force | Out-Null
$displayVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'Kiloview Job Configurator' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $displayVersion -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'John Lightfoot' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value $icon -PropertyType String -Force | Out-Null
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall-KiloviewSetup.ps1')`""
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Start-ScheduledTask -TaskName $scheduledTaskName
$healthy = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:8091/api/health' -TimeoutSec 1
        if ($health.status -eq 'ok') {
            $healthy = $true
            break
        }
    }
    catch { }
    Start-Sleep -Milliseconds 500
}
if (-not $healthy) { throw 'Kiloview Job Configurator was installed but did not start successfully on port 8091.' }
Start-Process 'http://localhost:8091'
Write-Host "Kiloview Job Configurator installed for the current user at $installRoot"
