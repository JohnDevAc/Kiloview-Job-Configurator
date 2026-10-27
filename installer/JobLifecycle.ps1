# Product-owned lifecycle primitives. Import loads only path-validation code.
if (-not ('NdiLifecycle.CloudPath' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'JobLifecycle.PathSafety.cs') }
function Assert-JobSafePath([string]$Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\') -or $Path.Substring([IO.Path]::GetPathRoot($Path).Length).Contains(':')) { throw 'An absolute local lifecycle path is required.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -and -not [NdiLifecycle.CloudPath]::IsAllowed($current)) { throw "Filesystem links are not allowed: $current" }
    }
    return $full
}

function Assert-JobSafeTree([string]$Path) {
    [void](Assert-JobSafePath $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and -not [NdiLifecycle.CloudPath]::IsAllowed($item.FullName)) { throw "Filesystem link in product directory: $($item.FullName)" }
        if ($item.PSIsContainer) { Assert-JobSafeTree $item.FullName }
    }
}

function Write-JobAtomicJson([string]$Path, $Value) {
    $full = Assert-JobSafePath $Path
    $temporary = "$full.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 20))
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $full) { [IO.File]::Replace($temporary, $full, [NullString]::Value) } else { [IO.File]::Move($temporary, $full) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

function Assert-JobOperationOwner([string]$Directory, [string]$OwnerSid) {
    [void](Assert-JobSafePath $Directory)
    $acl = Get-Acl -LiteralPath $Directory
    $allowed = @($OwnerSid, 'S-1-5-18', 'S-1-5-32-544')
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $allowed) { throw 'Lifecycle operation directory belongs to another account.' }
    $writes = [Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writes) -and $rule.IdentityReference.Value -notin ($allowed + 'S-1-3-0')) { throw 'Lifecycle operation directory permits writes by another account.' }
    }
}

function ConvertFrom-JobLifecycleJson([string]$Json) {
    # ConvertFrom-Json/PowerShell can unwrap a singleton root array. Require the
    # JSON object envelope before deserializing, then inspect types before casts.
    if ($Json -notmatch '^\s*\{') { throw 'A lifecycle JSON object is required.' }
    $value = $Json | ConvertFrom-Json
    if ($value -isnot [pscustomobject]) { throw 'A lifecycle JSON object is required.' }
    return $value
}

function Assert-JobLifecycleIdentity($Value) {
    if ($Value -isnot [pscustomobject] -or ($Value.schemaVersion -isnot [int] -and $Value.schemaVersion -isnot [long]) -or $Value.schemaVersion -ne 1) {
        throw 'Lifecycle schemaVersion must be the scalar integer 1.'
    }
    foreach ($field in @('operationId','productId','action')) {
        if ($Value.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($Value.$field)) { throw "Lifecycle $field must be a non-empty string." }
    }
    $id = [Guid]::Empty
    if ($Value.productId -ne 'job' -or -not [Guid]::TryParseExact($Value.operationId, 'D', [ref]$id) -or $id -eq [Guid]::Empty -or $Value.action -notin @('inspect','install','update','repair','uninstall')) {
        throw 'Unsupported lifecycle product, operation identity or action.'
    }
}

function Read-JobLifecycleRequest([string]$Path) {
    $path = Assert-JobSafePath $Path
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Assert-JobOperationOwner (Split-Path -Parent $path) $owner
    if ((Get-Item -LiteralPath $path).Length -gt 16384) { throw 'Lifecycle request exceeds 16 KiB.' }
    $request = ConvertFrom-JobLifecycleJson (Get-Content -LiteralPath $path -Raw -Encoding UTF8)
    Assert-JobLifecycleIdentity $request
    if ($request.resultPath -isnot [string] -or [string]::IsNullOrWhiteSpace($request.resultPath)) { throw 'Lifecycle resultPath must be a non-empty string.' }
    if ($request.PSObject.Properties['preserveData'] -and $request.preserveData -isnot [bool]) { throw 'Lifecycle preserveData must be a Boolean.' }
    if ($request.PSObject.Properties['role'] -and ($request.role -isnot [string] -or $request.role -notin @('client','server'))) { throw 'Lifecycle role must be client or server.' }
    if ($request.PSObject.Properties['requestedVersion'] -and ($request.requestedVersion -isnot [string] -or [string]::IsNullOrWhiteSpace($request.requestedVersion))) { throw 'Lifecycle requestedVersion must be a non-empty string.' }
    $resultPath = Assert-JobSafePath $request.resultPath
    if ((Split-Path -Parent $resultPath) -ne (Split-Path -Parent $path) -or $resultPath -eq $path -or (Split-Path -Leaf $resultPath) -ne 'result.json') { throw 'The result must be result.json in the owned operation directory.' }
    if (Test-Path -LiteralPath $resultPath) {
        $prior = ConvertFrom-JobLifecycleJson (Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8)
        Assert-JobLifecycleIdentity $prior
        if ($prior.operationId -ne $request.operationId -or $prior.action -ne $request.action) { throw 'A mismatched lifecycle result already exists.' }
    }
    return $request
}

function Write-JobLifecycleResult($Request, [string]$Outcome, [string]$Message, [hashtable]$Evidence = @{}) {
    if (-not $Request) { return }
    Assert-JobOperationOwner (Split-Path -Parent $Request.resultPath) ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    $value = [ordered]@{schemaVersion=1; operationId=$Request.operationId; productId='job'; action=$Request.action; outcome=$Outcome; updatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); message=$Message}
    foreach ($key in $Evidence.Keys) { $value[$key] = $Evidence[$key] }
    Write-JobAtomicJson $Request.resultPath $value
}

function Assert-JobWorkerExit($Request,[int]$ExitCode) {
    if(-not $Request){return}
    [void](Assert-JobSafePath $Request.resultPath)
    $result=ConvertFrom-JobLifecycleJson (Get-Content -LiteralPath $Request.resultPath -Raw -Encoding UTF8)
    Assert-JobLifecycleIdentity $result
    if($result.operationId -ne $Request.operationId -or $result.action -ne $Request.action -or $result.outcome -isnot [string] -or $result.outcome -notin @('succeeded','no-change','cancelled','failed','partial','blocked','restart-required')) {
        throw 'The elevated worker exited without a final correlated result. Inspect the installation before retrying.'
    }
    if($ExitCode -ne 0 -and $result.outcome -in @('succeeded','no-change')){throw 'The worker failed after reporting completion. Inspect the actual installation before retrying.'}
}

function Assert-JobDesktopOwner {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session = (Get-Process -Id $PID).SessionId
    $owners = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object SessionId -eq $session | ForEach-Object {
        $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid
        if ($owner.ReturnValue -ne 0) { throw 'The signed-in desktop owner could not be verified.' }
        $owner.Sid
    } | Select-Object -Unique)
    if ($owners.Count -ne 1 -or $owners[0] -ne $sid) { throw 'Run Job Configurator maintenance from its signed-in administrator desktop. Alternate-account UAC cannot modify this per-user server.' }
    return $sid
}

function Enter-JobLifecycleLease([string]$Directory) {
    [void](Assert-JobSafePath $Directory)
    [void][IO.Directory]::CreateDirectory($Directory)
    try { return [IO.FileStream]::new((Join-Path $Directory 'operation.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] { throw 'JOB_LIFECYCLE_BUSY: Another Job Configurator installation or removal is active.' }
}

function Get-JobPaths {
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    return @{ Install=(Join-Path $local 'Programs\NDI Job Configurator'); Recovery=(Join-Path $local 'NDI Job Configurator\Lifecycle'); Data=(Join-Path $local 'NDI Job Configurator') }
}

function Test-JobRecoveryRequired([string]$InstallRoot,[string]$RecoveryRoot) {
    $record=Read-JobReplacement $InstallRoot $RecoveryRoot
    return [bool](($record -and $record.phase -notin @('committed','restored')) -or (Test-Path -LiteralPath (Join-Path $RecoveryRoot 'removal.json')))
}

function Invoke-JobRemovalStep([string]$RecoveryRoot,[string]$Stage,[scriptblock]$Action) {
    $journal=Join-Path $RecoveryRoot 'removal.json'
    Write-JobAtomicJson $journal @{schemaVersion=1;phase=$Stage;updatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    try { & $Action }
    catch {
        Write-JobAtomicJson $journal @{schemaVersion=1;phase='partial';stage=$Stage;error=$_.Exception.Message;updatedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
        throw
    }
}

function Get-JobComponent([string]$InstallRoot) {
    $exe = Join-Path $InstallRoot 'NDIJobConfigurator.exe'
    $version = $null; $channel = $null; $running = $null; $healthy = $null
    if (Test-Path -LiteralPath $exe) {
        $info = (Get-Item -LiteralPath $exe).VersionInfo
        if ($info.ProductName -eq 'NDI Job Configurator') {
            $version = $info.ProductVersion.Split('+')[0]
            $channel = if ($version.Contains('-')) { 'Development' } else { 'Main' }
            $processes = @(Get-CimInstance Win32_Process -Filter "Name='NDIJobConfigurator.exe'")
            if (@($processes | Where-Object ExecutablePath -eq $exe).Count -gt 0) { $running=$true }
            elseif (@($processes | Where-Object { -not $_.ExecutablePath }).Count -gt 0) { $running=$null }
            else { $running=$false }
            if ($running -eq $true) {
                try {
                    $probe=Invoke-RestMethod 'http://127.0.0.1:8091/api/health' -TimeoutSec 2
                    if($probe.processId){$healthy=Test-JobExactHealth $InstallRoot $version $channel}
                } catch { $healthy=$false }
            } elseif ($running -eq $false) { $healthy=$false }
        }
    }
    return @{productId='job'; installed=($null -ne $version); version=$version; channel=$channel; configured=$null; running=$running; healthy=$healthy}
}

function Test-JobHealthEvidence($Health, [string]$Version, [string]$Channel, [string]$ExpectedPath, $Process, [string]$OwnerSid, [string]$ExpectedOwner, [int]$PortPid) {
    return $Health.status -eq 'ok' -and $Health.product -eq 'NDI Job Configurator' -and $Health.version -eq $Version -and $Health.channel -eq $Channel -and $Process -and $Health.processId -eq $Process.ProcessId -and $PortPid -eq $Process.ProcessId -and $Process.ExecutablePath -eq $ExpectedPath -and $OwnerSid -eq $ExpectedOwner
}

function Test-JobExactHealth([string]$InstallRoot, [string]$Version, [string]$Channel) {
    try {
        $health = Invoke-RestMethod 'http://127.0.0.1:8091/api/health' -TimeoutSec 2
        if (-not $health.processId) { return $false }
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$health.processId)"
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
        $listeners = @(Get-NetTCPConnection -LocalPort 8091 -State Listen -ErrorAction Stop)
        if ($listeners.Count -eq 0 -or @($listeners | Where-Object OwningProcess -ne $process.ProcessId).Count -gt 0) { return $false }
        return Test-JobHealthEvidence $health $Version $Channel (Join-Path $InstallRoot 'NDIJobConfigurator.exe') $process $owner.Sid ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) $listeners[0].OwningProcess
    } catch { return $false }
}

function Get-JobTreeHashes([string]$Directory) {
    Assert-JobSafeTree $Directory
    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Force) { $hashes[$file.FullName.Substring($Directory.TrimEnd('\').Length + 1)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    return $hashes
}

function Assert-JobTreeHashes([string]$Directory, $Hashes) {
    $actual = Get-JobTreeHashes $Directory
    $entries = if ($Hashes -is [hashtable]) { $Hashes.GetEnumerator() } else { $Hashes.PSObject.Properties | ForEach-Object { @{Key=$_.Name; Value=$_.Value} } }
    foreach ($entry in $entries) { if ($actual[$entry.Key] -ne $entry.Value) { throw "Product file failed verification: $($entry.Key)" } }
    if ($actual.Count -ne @($entries).Count) { throw 'Product file inventory differs from the expected payload.' }
}

function Read-JobReplacement([string]$InstallRoot, [string]$RecoveryRoot) {
    $journal = Join-Path $RecoveryRoot 'replacement.json'
    if (-not (Test-Path -LiteralPath $journal)) { return $null }
    [void](Assert-JobSafePath $journal)
    $record = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.schemaVersion -ne 1 -or $record.installRoot -ne (Assert-JobSafePath $InstallRoot) -or
        $record.backup -ne (Join-Path $RecoveryRoot 'previous') -or $record.staged -ne (Join-Path $RecoveryRoot 'staged')) { throw 'Invalid server replacement recovery journal.' }
    return $record
}

function Restore-JobReplacement([string]$InstallRoot, [string]$RecoveryRoot, [scriptblock]$Stop, [scriptblock]$RestoreIntegration) {
    $record = Read-JobReplacement $InstallRoot $RecoveryRoot
    if (-not $record -or $record.phase -in @('committed','restored')) { return }
    Assert-JobSafeTree $InstallRoot; Assert-JobSafeTree $record.backup; Assert-JobSafeTree $record.staged
    # The backup's presence proves the old directory was moved, even if a crash
    # prevented the following journal write. Never infer success from a phase.
    if ($record.hadPrevious -and (Test-Path -LiteralPath $record.backup)) {
        Assert-JobTreeHashes $record.backup $record.previousHashes
        & $Stop
        if (Test-Path -LiteralPath $InstallRoot) { Remove-Item -LiteralPath $InstallRoot -Recurse -Force }
        Move-Item -LiteralPath $record.backup -Destination $InstallRoot
    } elseif (-not $record.hadPrevious -and $record.phase -ne 'prepared') {
        & $Stop
        if (Test-Path -LiteralPath $InstallRoot) { Remove-Item -LiteralPath $InstallRoot -Recurse -Force }
    } elseif ($record.hadPrevious -and $record.phase -ne 'prepared') {
        Assert-JobTreeHashes $InstallRoot $record.previousHashes
    }
    & $RestoreIntegration $record.integration
    $record.phase = 'restored'
    Write-JobAtomicJson (Join-Path $RecoveryRoot 'replacement.json') $record
}

function Invoke-JobReplacement([string]$Source, [string]$InstallRoot, [string]$RecoveryRoot,
    [scriptblock]$Stop, [scriptblock]$Configure, [scriptblock]$Verify, $Integration, [scriptblock]$RestoreIntegration) {
    $install = Assert-JobSafePath $InstallRoot; $sourceRoot = Assert-JobSafePath $Source
    $recovery = Assert-JobSafePath $RecoveryRoot
    if ($sourceRoot -eq $install -or $sourceRoot.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Install from a verified extracted package outside the installed directory.' }
    Assert-JobSafeTree $sourceRoot; Assert-JobSafeTree $install
    [void][IO.Directory]::CreateDirectory($recovery)
    $existing = Read-JobReplacement $install $recovery
    if ($existing -and $existing.phase -notin @('committed','restored')) { throw 'Recover the previous interrupted server replacement before retrying.' }
    $staged = Join-Path $recovery 'staged'; $backup = Join-Path $recovery 'previous'
    foreach ($path in @($staged,$backup)) { Assert-JobSafeTree $path; if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
    [void][IO.Directory]::CreateDirectory($staged)
    Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $staged -Recurse -Force
    $expected = Get-JobTreeHashes $sourceRoot
    Assert-JobTreeHashes $staged $expected
    $hadPrevious = Test-Path -LiteralPath $install
    $record = @{schemaVersion=1; installRoot=$install; backup=$backup; staged=$staged; hadPrevious=$hadPrevious;
        previousHashes=$(if($hadPrevious){Get-JobTreeHashes $install}else{@{}}); phase='prepared'; integration=$Integration}
    Write-JobAtomicJson (Join-Path $recovery 'replacement.json') $record
    try {
        & $Stop
        if ($hadPrevious) { Move-Item -LiteralPath $install -Destination $backup }
        $record.phase='old-moved'; Write-JobAtomicJson (Join-Path $recovery 'replacement.json') $record
        Move-Item -LiteralPath $staged -Destination $install
        $record.phase='integrating'; Write-JobAtomicJson (Join-Path $recovery 'replacement.json') $record
        & $Configure
        & $Verify
        $record.phase='committed'; Write-JobAtomicJson (Join-Path $recovery 'replacement.json') $record
    } catch {
        $failure = $_
        try { Restore-JobReplacement $install $recovery $Stop $RestoreIntegration }
        catch { throw "Server replacement failed: $failure. Recovery is incomplete: $_. Keep the recovery directory and retry repair." }
        throw "Server replacement failed; the previous server files and owned integration were restored. $failure"
    }
}
