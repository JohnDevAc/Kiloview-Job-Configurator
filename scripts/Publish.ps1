[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SetupExe,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root 'artifacts'
$publish = Join-Path $artifactRoot 'KiloviewSetup'
$package = Join-Path $artifactRoot 'KiloviewSetup-Windows.zip'
$setup = Join-Path $artifactRoot 'KiloviewSetup-Setup.exe'

if ($SetupExe) { $SelfContained = $true }

if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
New-Item -ItemType Directory -Path $publish -Force | Out-Null

$arguments = @('publish', (Join-Path $root 'Kiloview.Setup.csproj'), '--configuration', $Configuration, '--output', $publish, '--configfile', (Join-Path $root 'NuGet.Config'))
if ($SelfContained) {
    $arguments += @('--runtime', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '--source', 'https://api.nuget.org/v3/index.json')
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item -LiteralPath (Join-Path $root 'installer\Install-KiloviewSetup.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Uninstall-KiloviewSetup.ps1') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'installer\Install.cmd') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish

if (Test-Path $package) { Remove-Item -LiteralPath $package -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $package -CompressionLevel Optimal
Write-Host "Package created: $package"

if ($SetupExe) {
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $setupSource = Join-Path $temporaryRoot ("KiloviewSetup-IExpress-" + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $setupSource -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'installer\Setup-Install.cmd') -Destination $setupSource
        Copy-Item -LiteralPath (Join-Path $root 'installer\Setup-Install.ps1') -Destination $setupSource
        Copy-Item -LiteralPath $package -Destination (Join-Path $setupSource 'KiloviewSetup-Payload.zip')

        $temporarySetup = Join-Path $setupSource 'KiloviewSetup-Setup.exe'
        $sed = Join-Path $setupSource 'KiloviewSetup.sed'
        $sourceWithSlash = $setupSource.TrimEnd('\') + '\'
        $sedContent = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=Kiloview Setup was installed and has been started.
TargetName=$temporarySetup
FriendlyName=Kiloview Setup
AppLaunched=cmd.exe /d /c Setup-Install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=cmd.exe /d /c Setup-Install.cmd
UserQuietInstCmd=cmd.exe /d /c Setup-Install.cmd
FILE0="Setup-Install.cmd"
FILE1="Setup-Install.ps1"
FILE2="KiloviewSetup-Payload.zip"
[SourceFiles]
SourceFiles0=$sourceWithSlash
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@
        Set-Content -LiteralPath $sed -Value $sedContent -Encoding ASCII
        $iexpress = Start-Process -FilePath "$env:WINDIR\System32\iexpress.exe" -ArgumentList @('/N', '/Q', $sed) -WindowStyle Hidden -Wait -PassThru
        if ($iexpress.ExitCode -ne 0 -or -not (Test-Path $temporarySetup)) { throw "IExpress failed to create Setup.exe (exit $($iexpress.ExitCode))." }
        Copy-Item -LiteralPath $temporarySetup -Destination $setup -Force
        Write-Host "Installer created: $setup"
    }
    finally {
        $resolvedSetupSource = [IO.Path]::GetFullPath($setupSource)
        if ($resolvedSetupSource.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedSetupSource)) {
            Remove-Item -LiteralPath $resolvedSetupSource -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
