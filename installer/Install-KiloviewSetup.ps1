[CmdletBinding()]
param([string]$Source = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Kiloview Setup'
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kiloview Setup'
$startup = [Environment]::GetFolderPath('Startup')
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Kiloview Setup'

$sourceExe = Join-Path $Source 'KiloviewSetup.exe'
if (-not (Test-Path $sourceExe)) { throw "KiloviewSetup.exe was not found in $Source. Run scripts\Publish.ps1 first." }

$runtimeConfig = Join-Path $Source 'KiloviewSetup.runtimeconfig.json'
if (Test-Path $runtimeConfig) {
    $runtimeSettings = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
    $needsSharedRuntime = $null -ne $runtimeSettings.runtimeOptions.framework -or $null -ne $runtimeSettings.runtimeOptions.frameworks
    if ($needsSharedRuntime) {
        $runtimes = & dotnet --list-runtimes 2>$null
        if (-not ($runtimes -match 'Microsoft\.AspNetCore\.App 8\.')) {
            throw 'The .NET 8 ASP.NET Core Runtime is required. Use Setup.exe or install the .NET 8 ASP.NET Core Runtime.'
        }
    }
}

Get-Process KiloviewSetup -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
Get-ChildItem -LiteralPath $Source -File | Where-Object Extension -in '.exe','.dll','.json','.pdb' | Copy-Item -Destination $installRoot -Force
if (Test-Path (Join-Path $Source 'wwwroot')) { Copy-Item -LiteralPath (Join-Path $Source 'wwwroot') -Destination $installRoot -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $Source 'Uninstall-KiloviewSetup.ps1') -Destination $installRoot -Force

$exe = Join-Path $installRoot 'KiloviewSetup.exe'
$shell = New-Object -ComObject WScript.Shell
$startupShortcut = $shell.CreateShortcut((Join-Path $startup 'Kiloview Setup Service.lnk'))
$startupShortcut.TargetPath = Join-Path $PSHOME 'powershell.exe'
$startupShortcut.Arguments = "-NoProfile -WindowStyle Hidden -Command `"& '$exe'`""
$startupShortcut.WorkingDirectory = $installRoot
$startupShortcut.WindowStyle = 0
$startupShortcut.Description = 'Kiloview Setup local web service'
$startupShortcut.Save()

$url = @"
[InternetShortcut]
URL=http://localhost:8091
IconFile=$exe
IconIndex=0
"@
Set-Content -LiteralPath (Join-Path $desktop 'Kiloview Setup.url') -Value $url -Encoding ASCII
Set-Content -LiteralPath (Join-Path $startMenu 'Kiloview Setup.url') -Value $url -Encoding ASCII

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KiloviewSetup'
New-Item -Path $uninstallKey -Force | Out-Null
$displayVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'Kiloview Setup' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $displayVersion -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'JohnDevAc' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value $exe -PropertyType String -Force | Out-Null
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall-KiloviewSetup.ps1')`""
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Start-Process -FilePath $exe -WorkingDirectory $installRoot -WindowStyle Hidden
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
if (-not $healthy) { throw 'Kiloview Setup was installed but did not start successfully on port 8091.' }
Start-Process 'http://localhost:8091'
Write-Host "Kiloview Setup installed for the current user at $installRoot"
