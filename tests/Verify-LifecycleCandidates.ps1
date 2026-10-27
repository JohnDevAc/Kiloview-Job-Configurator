[CmdletBinding()]
param([Parameter(Mandatory)][string]$ServerPackage,[Parameter(Mandatory)][string]$AgentPackage,[Parameter(Mandatory)][string]$ServerInstaller)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\installer\JobLifecycle.ps1')
. (Join-Path $PSScriptRoot '..\installer\JobLifecycle.Package.ps1')
. (Join-Path $PSScriptRoot '..\installer\PcAgentPackage.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('ndi-lifecycle-candidates-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$server=Join-Path $root 'server';$agent=Join-Path $root 'agent'
Expand-Archive -LiteralPath $ServerPackage -DestinationPath $server
Expand-Archive -LiteralPath $AgentPackage -DestinationPath $agent
$serverManifest=Test-JobPackage $server
$companionManifest=Test-PcAgentPackage $server
$agentArchiveHash=(Get-FileHash -LiteralPath $AgentPackage -Algorithm SHA256).Hash
$agentArchiveBytes=(Get-Item -LiteralPath $AgentPackage).Length
if($companionManifest.sourceArchiveSha256 -ne $agentArchiveHash -or $companionManifest.sourceArchiveBytes -ne $agentArchiveBytes){throw 'Bundled companion provenance does not identify the frozen standalone archive.'}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\installer') -File | Where-Object { $_.Extension -eq '.ps1' -or $_.Name -in @('JobLifecycle.PathSafety.cs','toolbox-lifecycle.json') }) {
    if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $server $file.Name)).Hash){throw "Candidate contains stale maintenance source: $($file.Name)"}
}
$standaloneHashes=Get-JobTreeHashes $agent
Assert-JobTreeHashes (Join-Path $server 'pc-onboarding') $standaloneHashes
function Test-Descriptor([string]$Directory,[string]$Product,[string]$Kind){
    $descriptor=Get-Content -LiteralPath (Join-Path $Directory 'toolbox-lifecycle.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $entry=Assert-JobSafePath (Join-Path $Directory $descriptor.entryPoint)
    if($descriptor.schemaVersion -ne 1 -or $descriptor.productId -ne $Product -or $descriptor.entryPointKind -ne $Kind -or
        -not $entry.StartsWith($Directory+'\',[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $entry) -or
        @($descriptor.supportedActions).Count -ne 5){throw 'Package descriptor does not resolve to its owned entry point.'}
    return $entry
}
$serverEntry=Test-Descriptor $server 'job' 'powershell'
$agentEntry=Test-Descriptor $agent 'pc-agent' 'native'
$prepared=New-JobPayload $server (Join-Path $root 'isolated-preparation') $serverManifest
$installedDescriptor=Get-Content -LiteralPath (Join-Path $prepared 'toolbox-lifecycle.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if(@($installedDescriptor.supportedActions).Count -ne 2 -or -not (Test-Path -LiteralPath (Join-Path $prepared $installedDescriptor.entryPoint))){throw 'Prepared installed maintenance descriptor is invalid.'}
$agentVersion=(Get-Item -LiteralPath (Join-Path $agent 'Agent\NDI Configurator PC Agent.exe')).VersionInfo.ProductVersion
$setupVersion=(Get-Item -LiteralPath $agentEntry).VersionInfo.ProductVersion
if((Compare-PcAgentVersion $agentVersion $setupVersion) -ne 0){throw 'Standalone Agent package has a mixed pair.'}
if((Get-Item -LiteralPath $ServerInstaller).VersionInfo.ProductVersion.Split('+')[0] -ne $serverManifest.version){throw 'Bootstrapper version differs from package.'}

function New-InspectRequest([string]$Product){
    $id=[Guid]::NewGuid().ToString('D');$operation=Join-Path $root $id;[void][IO.Directory]::CreateDirectory($operation)
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $owner=[Security.Principal.WindowsIdentity]::GetCurrent().User;$acl.SetOwner($owner)
    foreach($sid in @($owner.Value,'S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
    Set-Acl -LiteralPath $operation -AclObject $acl
    $request=@{schemaVersion=1;operationId=$id;productId=$Product;action='inspect';resultPath=(Join-Path $operation 'result.json')}
    Write-JobAtomicJson (Join-Path $operation 'request.json') $request
    return @{path=(Join-Path $operation 'request.json');request=$request}
}
$observations=@()
foreach($product in @('job','pc-agent')){
    $operation=New-InspectRequest $product
    $start=[Diagnostics.ProcessStartInfo]::new();$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    if($product -eq 'job'){
        $start.FileName='powershell.exe';$start.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$serverEntry+'" -LifecycleRequestPath "'+$operation.path+'"'
    }else{$start.FileName=$agentEntry;$start.Arguments='--lifecycle-request "'+$operation.path+'"'}
    $process=[Diagnostics.Process]::Start($start)
    try{
        if(-not $process.WaitForExit(30000)){ $process.Kill();throw 'Read-only inspection exceeded its bounded test wait.' }
        if($process.ExitCode -ne 0){throw "Native $product inspect failed with exit $($process.ExitCode)."}
        $result=Get-Content -LiteralPath $operation.request.resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if($result.schemaVersion -ne 1 -or $result.productId -ne $product -or $result.operationId -ne $operation.request.operationId -or $result.action -ne 'inspect' -or $result.outcome -ne 'succeeded'){throw 'Native inspect returned uncorrelated/failed evidence.'}
        $observations+= $result
    }finally{$process.Dispose()}
}
$assets=@($ServerPackage,$AgentPackage,$ServerInstaller) | ForEach-Object { @{path=[IO.Path]::GetFullPath($_);bytes=(Get-Item -LiteralPath $_).Length;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} }
$receipt=@{verifiedUtc=[DateTimeOffset]::UtcNow.ToString('O');serverVersion=$serverManifest.version;agentVersion=$companionManifest.version;bundleMatchesStandalone=$true;companionSource=@{archiveSha256=$agentArchiveHash;archiveBytes=$agentArchiveBytes;commit=$companionManifest.commit;workingTreeChanges=$companionManifest.workingTreeChanges};descriptorsVerified=$true;observations=$observations;assets=$assets;extractedRoot=$root}
Write-JobAtomicJson (Join-Path $root 'verification.json') $receipt
$receipt | ConvertTo-Json -Depth 10
