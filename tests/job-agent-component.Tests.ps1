$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\installer\JobLifecycle.ps1')
. (Join-Path $PSScriptRoot '..\installer\JobLifecycle.Package.ps1')
. (Join-Path $PSScriptRoot '..\installer\PcAgentPackage.ps1')
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
$root=Join-Path ([IO.Path]::GetTempPath()) ('job-agent-component-'+[Guid]::NewGuid().ToString('N'))
$package=Join-Path $root 'package';$installed=Join-Path $root 'installed'
$component=Join-Path $package 'pc-onboarding'
foreach($path in @($component,(Join-Path $component 'Agent'),$installed)){[void][IO.Directory]::CreateDirectory($path)}
$setup=Join-Path $component 'NDI Configurator PC Agent Setup.exe'
$fixtureSource=@'
using System;
using System.IO;
using System.Reflection;
[assembly: AssemblyVersion("0.7.5.0")]
[assembly: AssemblyInformationalVersion("0.7.5-dev.1")]
public static class HarmlessAgentComponent {
    public static int Main(string[] args) {
        if (args.Length != 1 || args[0] != "--server-command") return 4;
        string input;
        using (var reader = new StreamReader(Console.OpenStandardInput())) input = reader.ReadToEnd();
        string root = AppDomain.CurrentDomain.BaseDirectory;
        File.WriteAllText(Path.Combine(root, "received-stdin.json"), input);
        if (input != "{\"schemaVersion\":1,\"operation\":\"install\",\"acceptLicense\":true}") return 5;
        string mode = File.ReadAllText(Path.Combine(root, "fixture-mode.txt")).Trim();
        if (mode == "failed") { Console.WriteLine("{\"schemaVersion\":1,\"success\":false,\"error\":\"fixture failure after child start\"}"); return 1; }
        Console.WriteLine("{\"schemaVersion\":1,\"success\":true}"); return 0;
    }
}
'@
Add-Type -TypeDefinition $fixtureSource -OutputAssembly $setup -OutputType ConsoleApplication
Copy-Item -LiteralPath $setup -Destination (Join-Path $component 'Agent\NDI Configurator PC Agent.exe')
[IO.File]::WriteAllText((Join-Path $component 'LICENSE.md'),'Harmless native fixture; no installation, elevation, startup or firewall changes.')
$manifest=@{schemaVersion=1;localCommandSchema=1;repository='JohnDevAc/Kiloview-PC-Onboarding';version='0.7.5-dev.1';files=@(
    foreach($file in @('NDI Configurator PC Agent Setup.exe','Agent\NDI Configurator PC Agent.exe','LICENSE.md')){
        @{path=$file;sha256=(Get-FileHash -LiteralPath (Join-Path $component $file)).Hash}
    }
)}
Write-JobAtomicJson (Join-Path $package 'pc-onboarding-manifest.json') $manifest
foreach($file in @('NDI Configurator PC Agent Setup.exe','NDI Configurator PC Agent.exe')){Copy-Item -LiteralPath $setup -Destination (Join-Path $installed $file)}
$modePath=Join-Path $component 'fixture-mode.txt';$received=Join-Path $component 'received-stdin.json'
[IO.File]::WriteAllText($modePath,'success')
$effects=@();$evidence=$null
Write-Output "Evidence: $root"
$result=Install-JobAgentComponent $package ([ref]$effects) ([ref]$evidence) $installed
Assert (Test-Path -LiteralPath $received) 'Real native child was not invoked'
Assert (@($result.effects).Count -eq 1 -and $result.effects[0] -like 'Installed*' -and $result.effects[0] -is [string]) 'Successful component effect was lost or remained a reference object'
Assert ($effects.Count -eq 1 -and $effects[0] -eq $result.effects[0]) 'Caller effects were not updated on success'
Assert ($evidence.installed -eq $true -and $evidence.version -eq '0.7.5-dev.1') 'Caller component evidence was not updated'
Write-JobAtomicJson (Join-Path $root 'successful-component.json') @{result=$result;effects=$effects;component=$evidence}
Write-Output 'PASS native component child, schema1 input and reference success outputs'

$originalInputEncoding=[Console]::InputEncoding
try {
    foreach($emitBom in @($false,$true)) {
        [Console]::InputEncoding=[Text.UTF8Encoding]::new($emitBom)
        $effects=@();$evidence=$null
        $result=Install-JobAgentComponent $package ([ref]$effects) ([ref]$evidence) $installed
        Assert ($result.component.installed -and $evidence.version -eq '0.7.5-dev.1' -and $effects.Count -eq 1) 'UTF-8 redirected input failed or lost component evidence'
        Assert ([IO.File]::ReadAllText($received) -eq '{"schemaVersion":1,"operation":"install","acceptLicense":true}') 'Fixture did not decode the server-command stream like the real Agent'
        Write-Output "PASS native component with UTF-8 console input, BOM=$emitBom"
    }
} finally { [Console]::InputEncoding=$originalInputEncoding }

[IO.File]::WriteAllText($modePath,'failed');Remove-Item -LiteralPath $received
$effects=@();$evidence=$null
try { [void](Install-JobAgentComponent $package ([ref]$effects) ([ref]$evidence) $installed);throw 'CHILD_FAILURE_ACCEPTED' }
catch { if($_.Exception.Message -notlike '*fixture failure after child start*'){throw} }
Assert ((Test-Path -LiteralPath $received) -and $effects.Count -eq 1 -and $effects[0] -like '*installer was started*') 'Failed child lost partial-effect evidence'
Assert ($evidence.productId -eq 'pc-agent' -and $null -eq $evidence.installed) 'Failed child fabricated installed state'
Write-JobAtomicJson (Join-Path $root 'failed-component.json') @{effects=$effects;component=$evidence;outcome=$(if($effects.Count -gt 0){'partial'}else{'failed'})}
Write-Output 'PASS failed native child preserves caller partial effects and unknown component state'

$newer=Join-Path $root 'newer.exe'
$newerSource=$fixtureSource.Replace('0.7.5.0','0.9.0.0').Replace('0.7.5-dev.1','0.9.0').Replace('HarmlessAgentComponent','HarmlessNewerAgentComponent')
Add-Type -TypeDefinition $newerSource -OutputAssembly $newer -OutputType ConsoleApplication
foreach($file in @('NDI Configurator PC Agent Setup.exe','NDI Configurator PC Agent.exe')){Copy-Item -LiteralPath $newer -Destination (Join-Path $installed $file) -Force}
Remove-Item -LiteralPath $received
$effects=@();$evidence=$null
$result=Install-JobAgentComponent $package ([ref]$effects) ([ref]$evidence) $installed
Assert (-not (Test-Path -LiteralPath $received)) 'A newer independent Agent pair was executed or downgraded'
Assert ($effects.Count -eq 1 -and $effects[0] -like 'Retained*' -and $evidence.version -eq '0.9.0' -and $result.component.installed) 'Retained component reference outputs were incorrect'
Write-Output 'PASS newer complete independent pair is retained with reference evidence'

$manifest.files[0].sha256='0'*64
Write-JobAtomicJson (Join-Path $package 'pc-onboarding-manifest.json') $manifest
$effects=@();$evidence=$null
try { [void](Install-JobAgentComponent $package ([ref]$effects) ([ref]$evidence) $installed);throw 'BAD_PACKAGE_ACCEPTED' }
catch { if($_.Exception.Message -notlike '*checksum failed*'){throw} }
Assert ($effects.Count -eq 0 -and $null -eq $evidence -and -not (Test-Path -LiteralPath $received)) 'Rejected package reached a native child or fabricated partial effects'
Write-Output 'PASS invalid companion package rejects before native invocation'
