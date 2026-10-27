[CmdletBinding(SupportsShouldProcess)]
param([string]$LifecycleRequestPath,[string]$RequestSha256)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'JobLifecycle.ps1')
. (Join-Path $PSScriptRoot 'JobLifecycle.Windows.ps1')
$request=$null;$lease=$null;$effects=@();$paths=Get-JobPaths
try{
    [void](Assert-JobDesktopOwner)
    if($LifecycleRequestPath){
        $request=Read-JobLifecycleRequest $LifecycleRequestPath
        if($request.action -ne 'uninstall'){throw 'This entry point only removes Job Configurator.'}
        if($RequestSha256 -and (Get-FileHash -LiteralPath $LifecycleRequestPath).Hash -ne $RequestSha256){throw 'Lifecycle intent changed during elevation.'}
        if($request.PSObject.Properties['preserveData'] -and -not $request.preserveData){throw 'User-data purging is not supported.'}
    }
    Assert-JobSafeTree $paths.Install
    # No elevation, result write, lock creation or process stop precedes this gate.
    if(-not $PSCmdlet.ShouldProcess($paths.Install,'Remove Job Configurator software, preserving user data and PC Agent')){exit 1602}
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
        Write-JobLifecycleResult $request 'running' 'Waiting for Windows administrator approval.' @{stage='waiting-input';safeToCancel=$true}
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Confirm:$false'
        if($LifecycleRequestPath){$arguments+=' -LifecycleRequestPath "'+$LifecycleRequestPath+'" -RequestSha256 '+(Get-FileHash -LiteralPath $LifecycleRequestPath).Hash}
        $worker=Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        Assert-JobWorkerExit $request $worker.ExitCode
        exit $worker.ExitCode
    }
    $lease=Enter-JobLifecycleLease $paths.Recovery
    $record=Read-JobReplacement $paths.Install $paths.Recovery
    if($record -and $record.phase -notin @('committed','restored')){throw 'Recover the interrupted server replacement before uninstalling.'}
    [void](Get-JobOwnedIntegration $paths.Install)
    Install-JobRetainedMaintenance $PSScriptRoot $paths.Install
    Write-JobAtomicJson (Join-Path $paths.Recovery 'removal.json') @{schemaVersion=1;phase='removing';updatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    Write-JobLifecycleResult $request 'running' 'Removing server software; user data and shared PC Agent remain.' @{stage='uninstall';safeToCancel=$false}
    Invoke-JobRemovalStep $paths.Recovery 'stop' {Stop-JobOwnedProcesses $paths.Install};$effects+='Stopped the owned server process.'
    Invoke-JobRemovalStep $paths.Recovery 'registration' {Remove-JobWindowsRegistration $paths.Install -KeepInstalledApps};$effects+='Removed owned tasks, firewall rules and shortcuts.'
    $errors=@()
    if(Test-Path -LiteralPath $paths.Install){
        foreach($item in Get-ChildItem -LiteralPath $paths.Install -Force){
            if($item.Name -in @('Maintenance','toolbox-lifecycle.json')){continue}
            try{
                Assert-JobSafeTree $item.FullName
                Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop
                if(Test-Path -LiteralPath $item.FullName){throw "Installed file remains: $($item.Name)"}
            }catch{$errors+=$_.Exception.Message}
        }
    }
    if($errors.Count -gt 0){throw ('Removal is incomplete; retry from the retained maintenance script. '+($errors -join '; '))}
    Remove-JobWindowsRegistration $paths.Install
    Remove-Item -LiteralPath (Join-Path $paths.Recovery 'removal.json') -Force
    $effects+='Removed server application files and Installed Apps registration; retained maintenance entry point.'
    Write-JobLifecycleResult $request 'succeeded' 'Job Configurator removed. User data, shared PC Agent and NDI settings were retained.' @{stage='complete';components=@((Get-JobComponent $paths.Install));partialEffects=$effects;recoveryRequired=$false}
    Write-Host 'Job Configurator removed. User data, shared PC Agent and NDI settings were retained.'
    exit 0
}catch{
    $message=$_.Exception.Message;$busy=$message -like '*JOB_LIFECYCLE_BUSY*';$cancelled=$message -match '(?i)cancel|declined'
    $recovery=try{Test-JobRecoveryRequired $paths.Install $paths.Recovery}catch{$true}
    $outcome=if($cancelled){'cancelled'}elseif($busy){'blocked'}elseif($effects.Count -or $recovery){'partial'}else{'failed'}
    if(-not $WhatIfPreference){Write-JobLifecycleResult $request $outcome $message @{errorCode=$(if($busy){'operation-active'}else{'removal-failed'});partialEffects=$effects;recoveryRequired=$recovery}}
    Write-Error $message -ErrorAction Continue
    if($cancelled){exit 1602};if($busy){exit 1618};exit 1
}finally{if($lease){$lease.Dispose()}}
