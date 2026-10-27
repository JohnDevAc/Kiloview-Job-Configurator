[CmdletBinding()]
param([string]$Source=$PSScriptRoot,[switch]$SkipPcAgent,[switch]$LicenseAccepted,[string]$LifecycleRequestPath,[string]$RequestSha256)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'JobLifecycle.ps1')
. (Join-Path $PSScriptRoot 'JobLifecycle.Windows.ps1')
. (Join-Path $PSScriptRoot 'JobLifecycle.Package.ps1')
. (Join-Path $PSScriptRoot 'PcAgentPackage.ps1')
$request=$null;$lease=$null;$agentEffects=@();$agentEvidence=$null;$paths=Get-JobPaths
try {
    [void](Assert-JobDesktopOwner)
    if($LifecycleRequestPath) {
        $request=Read-JobLifecycleRequest $LifecycleRequestPath
        if($request.action -notin @('install','update','repair')){throw 'Unsupported installer action.'}
        if($RequestSha256 -and (Get-FileHash -LiteralPath $LifecycleRequestPath).Hash -ne $RequestSha256){throw 'Lifecycle intent changed during elevation.'}
        if($request.PSObject.Properties['preserveData'] -and -not $request.preserveData){throw 'Data purging is not supported.'}
    }
    $sourceRoot=Assert-JobSafePath $Source
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-JobLifecycleResult $request 'running' 'Waiting for Windows administrator approval.' @{stage='waiting-input';safeToCancel=$true}
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Source "'+$sourceRoot+'"'
        if($SkipPcAgent){$arguments+=' -SkipPcAgent'}
        if($LicenseAccepted){$arguments+=' -LicenseAccepted'}
        if($LifecycleRequestPath){$arguments+=' -LifecycleRequestPath "'+$LifecycleRequestPath+'" -RequestSha256 '+(Get-FileHash -LiteralPath $LifecycleRequestPath).Hash}
        $worker=Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        Assert-JobWorkerExit $request $worker.ExitCode
        exit $worker.ExitCode
    }
    $lease=Enter-JobLifecycleLease $paths.Recovery
    $stop={Stop-JobOwnedProcesses $paths.Install}
    $restore={param($saved) Restore-JobWindowsRegistration $paths.Install $saved}
    Restore-JobReplacement $paths.Install $paths.Recovery $stop $restore
    Write-JobLifecycleResult $request 'running' 'Validating the complete server package.' @{stage='preflight';safeToCancel=$true}
    $manifest=Test-JobPackage $sourceRoot
    if($request -and $request.requestedVersion -and $request.requestedVersion -ne $manifest.version){throw 'Requested server version differs from this verified package.'}
    $payload=New-JobPayload $sourceRoot $paths.Recovery $manifest
    if(-not $LicenseAccepted) {
        Write-JobLifecycleResult $request 'running' 'Waiting for licence acceptance and optional PC Agent selection.' @{stage='waiting-input';safeToCancel=$true}
        . (Join-Path $sourceRoot 'Select-InstallOptions.ps1')
        $SkipPcAgent=-not (Select-InstallOptions -PackageRoot $sourceRoot -IncludePcAgent (-not $SkipPcAgent))
    }
    if(-not $SkipPcAgent) {
        Write-JobLifecycleResult $request 'running' 'Installing or retaining the independent PC Agent component.' @{stage='pc-agent';safeToCancel=$false}
        $agentResult=Install-JobAgentComponent $sourceRoot ([ref]$agentEffects) ([ref]$agentEvidence)
        $agentEffects=@($agentResult.effects);$agentEvidence=$agentResult.component
    }
    $integration=Get-JobOwnedIntegration $paths.Install
    Write-JobLifecycleResult $request 'running' 'Replacing the validated server package; rollback material is retained.' @{stage='replacement';safeToCancel=$false;partialEffects=$agentEffects}
    $configure={Set-JobWindowsRegistration $paths.Install}
    $verify={
        for($attempt=0;$attempt -lt 20;$attempt++){
            if(Test-JobExactHealth $paths.Install $manifest.version $manifest.channel){return}
            Start-Sleep -Milliseconds 500
        }
        throw 'The expected server version, channel, process path, owner and listening port did not become healthy.'
    }
    Invoke-JobReplacement $payload $paths.Install $paths.Recovery $stop $configure $verify $integration $restore
    $removalJournal=Join-Path $paths.Recovery 'removal.json'
    if(Test-Path -LiteralPath $removalJournal){Remove-Item -LiteralPath $removalJournal -Force}
    $components=@((Get-JobComponent $paths.Install));if($agentEvidence){$components+=$agentEvidence}
    Write-JobLifecycleResult $request 'succeeded' 'Job Configurator installed and its exact process verified healthy.' @{stage='complete';components=$components;partialEffects=$agentEffects;recoveryRequired=$false}
    if(-not $request){Start-Process 'http://localhost:8091'}
    Write-Host 'Job Configurator installed. Existing user data and independent shared components were retained.'
    exit 0
}catch{
    $message=$_.Exception.Message;$cancelled=$message -match '(?i)cancel|declined';$busy=$message -like '*JOB_LIFECYCLE_BUSY*'
    $record=try{Read-JobReplacement $paths.Install $paths.Recovery}catch{$null}
    $recovery=try{Test-JobRecoveryRequired $paths.Install $paths.Recovery}catch{$true}
    $outcome=if($cancelled){'cancelled'}elseif($busy){'blocked'}elseif($agentEffects.Count -gt 0 -or $recovery){'partial'}else{'failed'}
    $components=@();if($agentEvidence){$components+=$agentEvidence}
    Write-JobLifecycleResult $request $outcome $message @{errorCode=$(if($busy){'operation-active'}else{'installation-failed'});partialEffects=$agentEffects;components=$components;recoveryRequired=$recovery}
    Write-Error $message -ErrorAction Continue
    if($cancelled){exit 1602};if($busy){exit 1618};exit 1
}finally{if($lease){$lease.Dispose()}}
