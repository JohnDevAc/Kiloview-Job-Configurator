[CmdletBinding()]
param([Parameter(Mandatory)][string]$LifecycleRequestPath,[ValidateSet('succeeded','cancelled','partial','blocked','restart-required','crash')][string]$FixtureOutcome='succeeded')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\installer\JobLifecycle.ps1')
$request=Read-JobLifecycleRequest $LifecycleRequestPath
Write-JobLifecycleResult $request 'running' 'Isolated producer fixture; no application operations are performed.' @{stage='fixture';safeToCancel=$true}
if($FixtureOutcome -eq 'crash'){exit 17}
Write-JobLifecycleResult $request $FixtureOutcome 'Isolated producer fixture completed.' @{stage='fixture';components=@(@{productId='job';installed=$true;configured=$null;running=$false;healthy=$null;version='fixture';channel='Development'});partialEffects=@()}
switch($FixtureOutcome){'succeeded'{exit 0};'cancelled'{exit 1602};'blocked'{exit 1618};'restart-required'{exit 3010};default{exit 1}}
