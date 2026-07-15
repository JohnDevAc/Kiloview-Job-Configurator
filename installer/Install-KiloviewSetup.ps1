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
    $runtimes = & dotnet --list-runtimes 2>$null
    if (-not ($runtimes -match 'Microsoft\.AspNetCore\.App 8\.')) {
        throw 'The .NET 8 ASP.NET Core Runtime is required. Use a self-contained package or install the .NET 8 Hosting Bundle.'
    }
}

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

Start-Process -FilePath $exe -WorkingDirectory $installRoot -WindowStyle Hidden
Start-Sleep -Seconds 2
Start-Process 'http://localhost:8091'
Write-Host "Kiloview Setup installed for the current user at $installRoot"
