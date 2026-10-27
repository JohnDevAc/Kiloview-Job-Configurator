$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('ndi-consent-dialog-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $root 'pc-onboarding'))
$serverLicense='TEST FIXTURE ONLY. No installation or acceptance. Licensor'+[char]0x2019+'s '+[char]0x201c+'Non-commercial'+[char]0x201d+' '+[char]0x2014+' caf'+[char]0x00e9
$agentLicense='TEST COMPONENT LICENCE '+[char]0x00a9+' 2026. Non'+[char]0x2011+'commercial.'
$utf8=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $root 'LICENSE.md'),$serverLicense,$utf8)
[IO.File]::WriteAllText((Join-Path $root 'pc-onboarding\LICENSE.md'),$agentLicense,$utf8)
foreach($mode in @(@{style='Hidden';shell=$false},@{style='Hidden';shell=$true},@{style='Normal';shell=$false})) {
    $evidence=Join-Path $root ($mode.style+'-shell'+$mode.shell+'.json')
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName='powershell.exe'
    $start.UseShellExecute=$mode.shell;$start.CreateNoWindow=$true;$start.WindowStyle=$mode.style
    $start.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot 'installer-dialog-fixture.ps1')+'" -PackageRoot "'+$root+'" -EvidencePath "'+$evidence+'"'
    $process=[Diagnostics.Process]::Start($start)
    try {
        if(-not $process.WaitForExit(10000)){$process.Kill();throw 'Consent dialog fixture stalled.'}
        if($process.ExitCode -ne 0){throw 'Consent dialog fixture failed.'}
        $result=Get-Content -LiteralPath $evidence -Raw -Encoding UTF8|ConvertFrom-Json
        if(-not $result.licenceText.Contains($serverLicense) -or -not $result.licenceText.Contains($agentLicense)) {
            throw "UTF-8 licence text was corrupted in the native dialog. Evidence: $evidence"
        }
        if(-not $result.cancelled -or @($result.observations).Count -eq 0 -or
            @($result.observations|Where-Object {-not $_.nativeVisible -or $_.minimized -or $_.consentChecked -or $_.installEnabled}).Count -gt 0) {
            throw 'Consent dialog was hidden or bypassed unchecked licence acceptance.'
        }
    }finally{$process.Dispose()}
}
Write-Output "PASS native consent visibility and UTF-8 licence text with hidden shell/direct launch, unchecked consent and cancellation. Evidence: $root"
