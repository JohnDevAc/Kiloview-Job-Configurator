param([ValidateSet('server-main','server-dev')][string]$Name)
$ErrorActionPreference='Stop'
$candidate=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'server-candidates.json') -Raw | ConvertFrom-Json) | Where-Object { $_.name -eq $Name }
$agent=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'agent-candidates.json') -Raw | ConvertFrom-Json) | Where-Object { $_.name -eq $Name.Replace('server-','agent-') }
$root=$candidate.root
$package=Join-Path $root 'artifacts/NDIJobConfigurator'
foreach($source in @($candidate,$agent)){
    if((& git.exe -C $source.root rev-parse HEAD).Trim() -ne $source.commit -or @(& git.exe -C $source.root status --porcelain).Count){throw 'Source changed or is dirty'}
}
. (Join-Path $package 'PcAgentPackage.ps1')
$manifest=Test-PcAgentPackage -PackageRoot $package
if($manifest.version -ne $agent.version -or $manifest.commit -ne $agent.commit -or $manifest.workingTreeChanges){throw 'Bundled companion provenance mismatch'}
$agentFiles=@(Get-ChildItem -LiteralPath (Join-Path $package 'pc-onboarding') -Recurse -File)
if($agentFiles.Count -ne $manifest.files.Count){throw 'Unmanifested companion files'}
foreach($relative in @('NDI Configurator PC Agent Setup.exe','Agent/NDI Configurator PC Agent.exe')){
    if((Get-Item -LiteralPath (Join-Path $package ('pc-onboarding/'+$relative))).VersionInfo.ProductVersion -ne $agent.version){throw 'Companion binary version mismatch'}
}
foreach($file in $agentFiles){
    $relative=$file.FullName.Substring((Join-Path $package 'pc-onboarding').Length+1)
    if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $agent.root ('artifacts/win-x64/'+$relative))).Hash){throw "Bundled companion differs from independent payload: $relative"}
}
foreach($path in @((Join-Path $package 'NDIJobConfigurator.exe'),(Join-Path $root 'artifacts/NDI-Job-Configurator.exe'))){
    if((Get-Item -LiteralPath $path).VersionInfo.ProductVersion -ne ($candidate.version+'+'+$candidate.commit)){throw 'Server or installer source stamp mismatch'}
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'wwwroot') -File -Recurse){
    $relative=$file.FullName.Substring($root.Length+1)
    if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $package $relative)).Hash){throw "Frontend source mismatch: $relative"}
}
$setupHash=(Get-FileHash -LiteralPath (Join-Path $root 'artifacts/NDI-Job-Configurator.exe')).Hash
if((Get-FileHash -LiteralPath (Join-Path $root 'artifacts/Kiloview-Job-Configurator.exe')).Hash -ne $setupHash){throw 'Legacy installer alias mismatch'}
$archivePath=Join-Path $root 'artifacts/NDI-Job-Configurator-Windows.zip'
$archiveHash=(Get-FileHash -LiteralPath $archivePath).Hash
$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $root 'installer/bin/Release/net8.0-windows/win-x64/NDI Job Configurator.dll')))
$stream=$assembly.GetManifestResourceStream('NDIJobConfigurator.Payload.zip')
if(-not $stream){throw 'Embedded payload is missing'}
try{$sha=[Security.Cryptography.SHA256]::Create();try{$embeddedHash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()}
if($embeddedHash -ne $archiveHash){throw 'Embedded payload differs from release ZIP'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archivePath)
try{
    $entries=@($zip.Entries | Where-Object {$_.Name})
    $files=@(Get-ChildItem -LiteralPath $package -File -Recurse)
    if($entries.Count -ne $files.Count){throw 'Package/archive file count mismatch'}
    foreach($file in $files){
        $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
        $entry=@($entries | Where-Object {$_.FullName.Replace('\','/') -ceq $relative})
        if($entry.Count -ne 1){throw "Missing or duplicate archive entry $relative"}
        $stream=$entry[0].Open()
        try{$sha=[Security.Cryptography.SHA256]::Create();try{$entryHash=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()}
        if($entryHash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Archive payload differs: $relative"}
    }
    $names=@($entries | ForEach-Object {$_.FullName.Replace('\','/')})
    foreach($required in @('NDIJobConfigurator.exe','wwwroot/js/local-onboarding.js','wwwroot/js/windows-cards.js','ONBOARDING-DIAGNOSTICS.md','pc-onboarding-manifest.json','pc-onboarding/NDI Configurator PC Agent Setup.exe','pc-onboarding/Agent/NDI Configurator PC Agent.exe')){if($required -notin $names){throw "Missing packaged file $required"}}
    if($names -match '(^|/)(tests|tmp|reports|\.git)/'){throw 'Scratch/source-control files leaked into package'}
}finally{$zip.Dispose()}
$assetPaths=@('NDI-Job-Configurator.exe','Kiloview-Job-Configurator.exe','NDI-Job-Configurator-Windows.zip' | ForEach-Object {Join-Path $root ('artifacts/'+$_)})
$checksums=Join-Path $root 'artifacts/SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksums,@($assetPaths | ForEach-Object {((Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant())+'  '+[IO.Path]::GetFileName($_)}),[Text.Encoding]::ASCII)
$assets=@($assetPaths+@($checksums) | ForEach-Object {$item=Get-Item -LiteralPath $_;@{name=$item.Name;path=$item.FullName;size=$item.Length;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
@{candidateOnly=$true;version=$candidate.version;channel=$candidate.channel;commit=$candidate.commit;clean=$true;root=$root;companionVersion=$agent.version;companionCommit=$agent.commit;companionFiles=$manifest.files.Count;embeddedPayloadMatches=$true;frontendMatches=$true;archiveSha256=$archiveHash;installerSha256=$setupHash;assets=$assets;entries=$names} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot ($Name+'-verification.json')) -Encoding UTF8
Write-Output "PASS ${Name}: clean source, clean matching companion, full manifest, source stamps, frontend, every archived byte, embedded payload and installer alias"
