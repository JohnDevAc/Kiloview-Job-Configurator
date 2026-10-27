function Test-JobPackage([string]$Source) {
    $root=Assert-JobSafePath $Source; Assert-JobSafeTree $root
    $manifest=Get-Content -LiteralPath (Join-Path $root 'job-package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.schemaVersion -ne 1 -or $manifest.productId -ne 'job' -or $manifest.channel -notin @('Main','Development') -or @($manifest.files).Count -lt 5){throw 'Invalid server package manifest.'}
    $info=(Get-Item -LiteralPath (Join-Path $root 'NDIJobConfigurator.exe')).VersionInfo
    if($info.ProductName -ne 'NDI Job Configurator' -or $info.ProductVersion.Split('+')[0] -ne $manifest.version){throw 'Server binary/version metadata mismatch.'}
    $seen=@{}
    foreach($file in $manifest.files){
        if(-not $file.path -or [IO.Path]::IsPathRooted($file.path) -or $file.path.Split([char[]]@('\','/')) -contains '..' -or $file.path.Contains(':')){throw 'Invalid package-relative filename.'}
        $path=Assert-JobSafePath (Join-Path $root $file.path)
        if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($path)){throw 'Duplicate or escaped package entry.'}
        $seen[$path]=$true
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){throw "Package hash mismatch: $($file.path)"}
    }
    foreach($required in @('NDIJobConfigurator.exe','wwwroot\index.html','Launch-NDIJobConfigurator.ps1','JobLifecycle.ps1','JobLifecycle.Windows.ps1','Invoke-NDIJobConfiguratorLifecycle.ps1','Uninstall-NDIJobConfigurator.ps1')){
        if(-not $seen.ContainsKey((Join-Path $root $required))){throw "Required server payload missing: $required"}
    }
    return $manifest
}

function New-JobPayload([string]$Source,[string]$RecoveryRoot,$Manifest){
    $payload=Assert-JobSafePath (Join-Path $RecoveryRoot 'incoming')
    $root=Assert-JobSafePath $Source
    if($root -eq $payload -or $root.StartsWith($payload+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'The source cannot be inside the preparation directory.'}
    Assert-JobSafeTree $payload
    if(Test-Path -LiteralPath $payload){Remove-Item -LiteralPath $payload -Recurse -Force}
    [void][IO.Directory]::CreateDirectory($payload)
    foreach($file in $Manifest.files){
        $destination=Join-Path $payload $file.path
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        Copy-Item -LiteralPath (Join-Path $root $file.path) -Destination $destination
        if((Get-FileHash -LiteralPath $destination).Hash -ne $file.sha256){throw 'Staged payload hash mismatch.'}
    }
    $runtime=Join-Path $payload 'NDIJobConfigurator.runtimeconfig.json'
    if(Test-Path -LiteralPath $runtime){
        $config=Get-Content -LiteralPath $runtime -Raw -Encoding UTF8 | ConvertFrom-Json
        if($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks){
            if(-not ((& dotnet --list-runtimes 2>$null) -match 'Microsoft\.AspNetCore\.App 8\.')){throw 'The .NET 8 ASP.NET Core Runtime is required.'}
        }
    }
    $maintenance=Join-Path $payload 'Maintenance';[void][IO.Directory]::CreateDirectory($maintenance)
    foreach($name in @('JobLifecycle.ps1','JobLifecycle.PathSafety.cs','JobLifecycle.Windows.ps1','Invoke-NDIJobConfiguratorLifecycle.ps1','Uninstall-NDIJobConfigurator.ps1')){Copy-Item -LiteralPath (Join-Path $payload $name) -Destination $maintenance}
    Write-JobAtomicJson (Join-Path $payload 'toolbox-lifecycle.json') @{schemaVersion=1;productId='job';supportedActions=@('inspect','uninstall');entryPoint='Maintenance/Invoke-NDIJobConfiguratorLifecycle.ps1';entryPointKind='powershell'}
    return $payload
}

function Install-JobAgentComponent([string]$Source,[ref]$Effects,[ref]$Evidence,
    [string]$InstalledDirectory=(Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'NDI Configurator\PC Agent')){
    $manifest=Test-PcAgentPackage $Source
    $root=Assert-JobSafePath $InstalledDirectory
    $agent=Join-Path $root 'NDI Configurator PC Agent.exe';$setup=Join-Path $root 'NDI Configurator PC Agent Setup.exe'
    $agentVersion=if(Test-Path -LiteralPath $agent){(Get-Item -LiteralPath $agent).VersionInfo.ProductVersion}else{$null}
    $setupVersion=if(Test-Path -LiteralPath $setup){(Get-Item -LiteralPath $setup).VersionInfo.ProductVersion}else{$null}
    # PowerShell variable names are case-insensitive: this accumulator must not
    # shadow the Effects reference, whose caller value survives child failures.
    $componentEffects=@()
    if((Get-PcAgentInstallDecision $agentVersion $setupVersion $manifest.version) -eq 'Retain'){$componentEffects+='Retained the newer independently installed PC Agent.'}
    else{
        $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=Join-Path $Source 'pc-onboarding\NDI Configurator PC Agent Setup.exe';$start.Arguments='--server-command';$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $process=[Diagnostics.Process]::Start($start)
        $Effects.Value=@('The independent PC Agent installer was started; its effects must be inspected if it fails or is interrupted.')
        $Evidence.Value=@{productId='pc-agent';installed=$null;configured=$null;running=$null;healthy=$null;version=$null;channel=$null}
        try{
            $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
            $process.StandardInput.Write('{"schemaVersion":1,"operation":"install","acceptLicense":true}');$process.StandardInput.Close()
            if(-not $process.WaitForExit(180000)){throw 'Independent PC Agent installation is still running. It was not killed; inspect its state before retrying.'}
            $response=$output.GetAwaiter().GetResult()|ConvertFrom-Json;[void]$errors.GetAwaiter().GetResult()
            if($process.ExitCode -ne 0 -or $response.schemaVersion -ne 1 -or -not $response.success){throw "Independent PC Agent installation failed or partially completed: $($response.error)"}
            $componentEffects+='Installed the independently owned PC Agent; server rollback does not downgrade it.'
        }finally{$process.Dispose()}
    }
    $Effects.Value=@($componentEffects)
    $agentVersion=(Get-Item -LiteralPath $agent).VersionInfo.ProductVersion;$setupVersion=(Get-Item -LiteralPath $setup).VersionInfo.ProductVersion
    $component=@{productId='pc-agent';installed=((Compare-PcAgentVersion $agentVersion $setupVersion)-eq 0);version=$agentVersion.Split('+')[0];setupVersion=$setupVersion.Split('+')[0];configured=$null;running=$null;healthy=$null}
    $Evidence.Value=$component
    return @{effects=@($componentEffects);component=$component}
}
