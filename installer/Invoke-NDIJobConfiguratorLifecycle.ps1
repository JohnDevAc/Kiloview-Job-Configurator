[CmdletBinding()]
param([Parameter(Mandatory)][string]$LifecycleRequestPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'JobLifecycle.ps1')
$request=$null
try {
    $request=Read-JobLifecycleRequest $LifecycleRequestPath
    $paths=Get-JobPaths
    if($request.action -eq 'inspect') {
        $recovery=Test-JobRecoveryRequired $paths.Install $paths.Recovery
        $safe=-not $recovery; $lease=$null
        $lock=Join-Path $paths.Recovery 'operation.lock'
        if(Test-Path -LiteralPath $lock) {
            try { $lease=[IO.FileStream]::new($lock,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None); $safe=-not $recovery }
            catch { $safe=$false }
            finally { if($lease){$lease.Dispose()} }
        }
        Write-JobLifecycleResult $request 'succeeded' 'Job Configurator inspected without changing installation or configuration.' @{stage='inspect'; components=@((Get-JobComponent $paths.Install)); recoveryRequired=$recovery; safeToRetry=$safe}
        exit 0
    }
    if($request.PSObject.Properties['preserveData'] -and -not $request.preserveData) { throw 'Lifecycle operations preserve application data and shared components. Purge is not supported.' }
    if($request.action -eq 'uninstall') { & (Join-Path $PSScriptRoot 'Uninstall-NDIJobConfigurator.ps1') -LifecycleRequestPath $LifecycleRequestPath; exit $LASTEXITCODE }
    if(-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'job-package-manifest.json'))) {
        Write-JobLifecycleResult $request 'blocked' 'Use the complete verified Job Configurator ZIP for install, update or repair.' @{errorCode='package-required'; nextAction='Download and verify the complete package, then retry.'}
        exit 1
    }
    & (Join-Path $PSScriptRoot 'Install-NDIJobConfigurator.ps1') -Source $PSScriptRoot -LifecycleRequestPath $LifecycleRequestPath
    exit $LASTEXITCODE
} catch {
    if($request) { Write-JobLifecycleResult $request 'failed' $_.Exception.Message @{errorCode='lifecycle-failed'} }
    Write-Error $_ -ErrorAction Continue
    exit 1
}
