$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\installer\JobLifecycle.ps1')
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Reject([scriptblock]$Action,[string]$Message){try{& $Action;throw "ACCEPTED: $Message"}catch{if($_.Exception.Message -like 'ACCEPTED:*'){throw}}}
$root=Join-Path ([IO.Path]::GetTempPath()) ('job-lifecycle-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$source=Join-Path $root 'source';$install=Join-Path $root 'installed';$recovery=Join-Path $root 'recovery'
foreach($path in @($source,$install,$recovery)){[void][IO.Directory]::CreateDirectory($path)}
[IO.File]::WriteAllText((Join-Path $source 'app.txt'),'new');[IO.File]::WriteAllText((Join-Path $install 'app.txt'),'old')
$script:stops=0;$script:restores=0
$stop={$script:stops++};$restore={param($snapshot) Assert ($snapshot.marker -eq 'old integration') 'Integration snapshot was lost';$script:restores++}
$snapshot=@{marker='old integration'}
Reject {Invoke-JobReplacement $source $install $recovery $stop {throw 'fixture registration failure'} {} $snapshot $restore} 'Configuration failure'
Assert ((Get-Content -LiteralPath (Join-Path $install 'app.txt') -Raw) -eq 'old') 'Failed registration did not restore old files'
Assert ($script:restores -eq 1) 'Integration rollback did not run'
Reject {Invoke-JobReplacement $source $install $recovery $stop {} {throw 'wrong process on port'} $snapshot $restore} 'Health failure'
Assert ((Get-Content -LiteralPath (Join-Path $install 'app.txt') -Raw) -eq 'old') 'Wrong health discarded old server'
Invoke-JobReplacement $source $install $recovery $stop {} {} $snapshot $restore
Assert ((Get-Content -LiteralPath (Join-Path $install 'app.txt') -Raw) -eq 'new') 'Successful replacement did not commit'
Assert ((Read-JobReplacement $install $recovery).phase -eq 'committed') 'Commit journal missing'
Write-Output 'PASS staged replacement, registration/health rollback and committed postcondition'

# Simulate a crash immediately after moving the old directory but before updating phase.
$record=Read-JobReplacement $install $recovery
$record.phase='prepared'
Write-JobAtomicJson (Join-Path $recovery 'replacement.json') $record
Restore-JobReplacement $install $recovery $stop $restore
Assert ((Get-Content -LiteralPath (Join-Path $install 'app.txt') -Raw) -eq 'old') 'Interrupted replacement was not recovered'
Write-Output 'PASS recovery from a crash between directory move and journal write'
foreach($stage in @('stop','task','firewall','shortcut')) {
    Reject {Invoke-JobRemovalStep $recovery $stage { [IO.File]::WriteAllText((Join-Path $root 'partial-effect.txt'),'effect happened'); throw 'fixture callback failed after effect' }} 'Partial removal callback'
    Assert (Test-JobRecoveryRequired $install $recovery) 'Partial removal was reported safe after callback failure'
    $removal=Get-Content -LiteralPath (Join-Path $recovery 'removal.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($removal.phase -eq 'partial' -and $removal.stage -eq $stage) 'Removal stage/effect was not durable'
}
Remove-Item -LiteralPath (Join-Path $recovery 'removal.json')
Write-Output 'PASS partial process/task/firewall/shortcut callbacks retain durable recovery'

$lease=Enter-JobLifecycleLease $recovery
try{Reject {Enter-JobLifecycleLease $recovery} 'Concurrent lease'}finally{$lease.Dispose()}
$lease=Enter-JobLifecycleLease $recovery;$lease.Dispose()
Write-Output 'PASS shared lifecycle lease excludes contenders and releases correctly'

$unicode='Unicode-'+[char]0xe9+'-'+[char]0x6d4b+[char]0x8bd5
$operation=Join-Path (Join-Path $root $unicode) ([Guid]::NewGuid().ToString('D'));[void][IO.Directory]::CreateDirectory($operation)
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
$currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl.SetOwner($currentSid)
foreach($sid in @($currentSid.Value,'S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
Set-Acl -LiteralPath $operation -AclObject $acl
$requestPath=Join-Path $operation 'request.json';$resultPath=Join-Path $operation 'result.json'
$request=@{schemaVersion=1;operationId=(Split-Path -Leaf $operation);productId='job';action='inspect';resultPath=$resultPath}
Write-JobAtomicJson $requestPath $request
$loaded=Read-JobLifecycleRequest $requestPath
Write-JobLifecycleResult $loaded 'running' 'fixture' @{stage='validation'}
Reject {Assert-JobWorkerExit $loaded 17} 'Worker exit without final receipt'
Write-JobLifecycleResult $loaded 'succeeded' 'fixture complete' @{components=@(@{productId='job';installed=$true;configured=$null;running=$false;healthy=$null;version='1.0';channel='Main'})}
$result=Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert ($result.operationId -eq $request.operationId -and $result.outcome -eq 'succeeded') 'Atomic correlated result was not retained'
Assert-JobWorkerExit $loaded 0
Reject {Assert-JobWorkerExit $loaded 1} 'Process failure contradicting success'
$result.operationId=[Guid]::NewGuid().ToString('D');Write-JobAtomicJson $resultPath $result
Reject {Read-JobLifecycleRequest $requestPath} 'Stale result'
$request.resultPath=Join-Path $root 'escape.json';Write-JobAtomicJson $requestPath $request
Reject {Read-JobLifecycleRequest $requestPath} 'Result path escape'
$request.schemaVersion=2;Write-JobAtomicJson $requestPath $request
Reject {Read-JobLifecycleRequest $requestPath} 'Unknown schema'
Write-Output 'PASS file correlation, stale result, escaped path and unknown schema'

# Use a valid envelope and no prior receipt so rejection proves type validation,
# not an unrelated path/correlation failure. JSON arrays must remain arrays.
Remove-Item -LiteralPath $resultPath
$valid=@{schemaVersion=1;operationId=(Split-Path -Leaf $operation);productId='job';action='install';resultPath=$resultPath}
$invalidFields=@(
    @{field='schemaVersion';value=$true},@{field='schemaVersion';value=@(1)},@{field='schemaVersion';value='1'},
    @{field='schemaVersion';value=$null},@{field='schemaVersion';value=@{}},
    @{field='operationId';value=@($valid.operationId)},@{field='operationId';value=$true},
    @{field='productId';value=@('job')},@{field='productId';value=1},
    @{field='action';value=@('install')},@{field='action';value=$true},
    @{field='resultPath';value=@($resultPath)},@{field='resultPath';value=$null},
    @{field='preserveData';value='false'},@{field='preserveData';value=@($true)},@{field='preserveData';value=1},@{field='preserveData';value=$null},
    @{field='role';value=@('client')},@{field='role';value=$true},@{field='role';value='unknown'},@{field='role';value=$null},
    @{field='requestedVersion';value=@('1.0')},@{field='requestedVersion';value=1},@{field='requestedVersion';value=''},@{field='requestedVersion';value=$null}
)
foreach($case in $invalidFields){
    $bad=$valid.Clone();$bad[$case.field]=$case.value;Write-JobAtomicJson $requestPath $bad
    Reject {Read-JobLifecycleRequest $requestPath} ("Malformed field: "+$case.field)
    Assert (-not (Test-Path -LiteralPath $resultPath)) 'Invalid request produced a receipt'
}
$json=$valid|ConvertTo-Json -Compress
foreach($badJson in @(('['+$json+']'),($json -replace '"schemaVersion":1','"schemaVersion":1.0'),($json -replace '"schemaVersion":1','"schemaVersion":1e0'))){
    [IO.File]::WriteAllText($requestPath,$badJson,[Text.UTF8Encoding]::new($false))
    Reject {Read-JobLifecycleRequest $requestPath} 'Non-object envelope or non-integer schema'
}
$valid.preserveData=$true;$valid.role='server';$valid.requestedVersion='1.0.0'
Write-JobAtomicJson $requestPath $valid
[void](Read-JobLifecycleRequest $requestPath)
# Exercise the real dispatch boundary, proving malformed install input never
# reaches even the harmless producer action or writes its running receipt.
foreach($case in $invalidFields | Select-Object -First 3){
    $bad=$valid.Clone();$bad[$case.field]=$case.value;Write-JobAtomicJson $requestPath $bad
    $previousPreference=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        $output=& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'job-lifecycle-fixture.ps1') -LifecycleRequestPath $requestPath -FixtureOutcome succeeded 2>&1
    } finally { $ErrorActionPreference=$previousPreference }
    Assert ($LASTEXITCODE -ne 0 -and -not (Test-Path -LiteralPath $resultPath)) 'Malformed install reached producer action'
}
Write-JobAtomicJson $requestPath $valid
$loaded=Read-JobLifecycleRequest $requestPath
foreach($badSchema in @($true,@(1),'1')){
    $badResult=@{schemaVersion=$badSchema;operationId=$valid.operationId;productId='job';action='install';outcome='succeeded'}
    Write-JobAtomicJson $resultPath $badResult
    Reject {Read-JobLifecycleRequest $requestPath} 'Malformed prior receipt identity'
    Reject {Assert-JobWorkerExit $loaded 0} 'Malformed elevated worker receipt identity'
}
Write-Output 'PASS strict JSON envelope, scalar field types and rejected producer dispatch'

Assert ([NdiLifecycle.CloudPath]::IsCloudTag(0x9000601aL) -and [NdiLifecycle.CloudPath]::IsCloudTag(0x9000e01aL)) 'Known cloud placeholders rejected'
Assert (-not [NdiLifecycle.CloudPath]::IsCloudTag(0xa0000003L) -and -not [NdiLifecycle.CloudPath]::IsCloudTag(0xa000000cL)) 'Junction/symlink tags allowed'
$junction=Join-Path $root 'redirect'
New-Item -ItemType Junction -Path $junction -Target $source | Out-Null
Reject {Assert-JobSafePath (Join-Path $junction 'app.txt')} 'Junction traversal'
[IO.Directory]::Delete($junction)
Write-Output 'PASS cloud tag allowlist and actual junction rejection'

$health=@{status='ok';product='NDI Job Configurator';version='1.0';channel='Main';processId=123}
$process=@{ProcessId=123;ExecutablePath='C:\owned\server.exe'}
Assert (Test-JobHealthEvidence $health '1.0' 'Main' 'C:\owned\server.exe' $process 'owner' 'owner' 123) 'Exact health rejected'
foreach($bad in @(@('2.0','Main','owner',123),@('1.0','Development','owner',123),@('1.0','Main','other',123),@('1.0','Main','owner',456))){
    Assert (-not (Test-JobHealthEvidence $health $bad[0] $bad[1] 'C:\owned\server.exe' $process $bad[2] 'owner' $bad[3])) 'Mismatched runtime accepted'
}
Write-Output 'PASS exact version/channel/owner/listener health checks'
Write-Output "Evidence retained in $root"
