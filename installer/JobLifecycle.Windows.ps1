# Windows integration is kept outside the file transaction so isolated fixtures
# can exercise replacement/recovery without touching processes, tasks or firewall.
function Get-JobOwnedIntegration([string]$InstallRoot) {
    $legacy = Join-Path (Split-Path -Parent $InstallRoot) 'Kiloview Setup\KiloviewSetup.exe'
    $expected = @((Join-Path $InstallRoot 'NDIJobConfigurator.exe'), $legacy)
    $sid = Assert-JobDesktopOwner
    $tasks = @()
    foreach ($name in @('NDI Job Configurator Service','Kiloview Job Configurator Service')) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if (-not $task) { continue }
        $taskSid = try { ([Security.Principal.NTAccount]$task.Principal.UserId).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $task.Principal.UserId }
        if ($taskSid -ne $sid -or @($task.Actions | Where-Object Execute -notin $expected).Count -gt 0) { throw "Scheduled task belongs to a different installation/account: $name" }
        $tasks += @{ name=$name; xml=(Export-ScheduledTask -TaskName $name); running=($task.State -eq 'Running') }
    }
    $rules = @()
    $policy = New-Object -ComObject HNetCfg.FwPolicy2
    try {
        foreach ($name in @('NDI Job Configurator LAN','NDI Job Configurator NDI','Kiloview Job Configurator LAN','Kiloview Job Configurator NDI')) {
            $matches = @($policy.Rules | Where-Object Name -eq $name)
            foreach ($rule in $matches) {
                if ($rule.ApplicationName -notin $expected) { throw "Firewall rule belongs to another executable: $name" }
                $data = @{}
                foreach ($property in @('Name','Description','ApplicationName','ServiceName','Protocol','LocalAddresses','RemoteAddresses','Direction','Enabled','Grouping','Profiles','EdgeTraversal','Action','InterfaceTypes')) { $data[$property] = $rule.$property }
                if ($rule.Protocol -in @(6,17)) { $data.LocalPorts=$rule.LocalPorts; $data.RemotePorts=$rule.RemotePorts }
                $rules += $data
            }
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($policy) }
    $shortcuts = @{}
    foreach ($path in Get-JobShortcutPaths) {
        [void](Assert-JobSafePath $path)
        $shortcuts[$path] = if (Test-Path -LiteralPath $path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) } else { $null }
    }
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NDIJobConfigurator'
    $registry = @{}
    if (Test-Path $key) { $item=Get-Item $key; foreach($name in $item.GetValueNames()) { $registry[$name]=@{value=$item.GetValue($name); kind=$item.GetValueKind($name).ToString()} } }
    return @{tasks=$tasks; rules=$rules; shortcuts=$shortcuts; registry=$registry}
}

function Get-JobShortcutPaths {
    return @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'NDI Job Configurator.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'NDI Job Configurator\NDI Job Configurator.lnk'))
}

function Install-JobRetainedMaintenance([string]$Source,[string]$InstallRoot) {
    $directory=Join-Path $InstallRoot 'Maintenance'
    Assert-JobSafeTree $directory
    [void][IO.Directory]::CreateDirectory($directory)
    foreach($name in @('JobLifecycle.ps1','JobLifecycle.PathSafety.cs','JobLifecycle.Windows.ps1','Invoke-NDIJobConfiguratorLifecycle.ps1','Uninstall-NDIJobConfigurator.ps1')) {
        $from=Assert-JobSafePath (Join-Path $Source $name);$to=Assert-JobSafePath (Join-Path $directory $name)
        if($from -ne $to){Copy-Item -LiteralPath $from -Destination $to -Force}
    }
    Write-JobAtomicJson (Join-Path $InstallRoot 'toolbox-lifecycle.json') @{schemaVersion=1;productId='job';supportedActions=@('inspect','uninstall');entryPoint='Maintenance/Invoke-NDIJobConfiguratorLifecycle.ps1';entryPointKind='powershell'}
}

function Stop-JobOwnedProcesses([string]$InstallRoot) {
    [void](Get-JobOwnedIntegration $InstallRoot)
    foreach ($name in @('NDI Job Configurator Service','Kiloview Job Configurator Service')) {
        if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $name }
    }
    $expected = @((Join-Path $InstallRoot 'NDIJobConfigurator.exe'), (Join-Path (Split-Path -Parent $InstallRoot) 'Kiloview Setup\KiloviewSetup.exe'))
    foreach ($process in Get-Process NDIJobConfigurator,KiloviewSetup -ErrorAction SilentlyContinue) {
        try {
            if ($process.Path -in $expected) { $process.Kill(); if (-not $process.WaitForExit(15000)) { throw 'The owned server process did not stop.' } }
        } finally { $process.Dispose() }
    }
}

function Remove-JobWindowsRegistration([string]$InstallRoot, [switch]$KeepInstalledApps) {
    $snapshot = Get-JobOwnedIntegration $InstallRoot
    foreach ($task in $snapshot.tasks) {
        Unregister-ScheduledTask -TaskName $task.name -Confirm:$false
        if (Get-ScheduledTask -TaskName $task.name -ErrorAction SilentlyContinue) { throw "Task remains: $($task.name)" }
    }
    foreach ($rule in $snapshot.rules) {
        Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction Stop | Remove-NetFirewallRule -ErrorAction Stop
        if (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue) { throw "Firewall rule remains: $($rule.Name)" }
    }
    foreach ($path in Get-JobShortcutPaths) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
    if (-not $KeepInstalledApps) {
        $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NDIJobConfigurator'
        if (Test-Path $key) { Remove-Item -LiteralPath $key -Recurse -Force }
        if (Test-Path $key) { throw 'Installed Apps registration remains.' }
    }
}

function Set-JobWindowsRegistration([string]$InstallRoot) {
    Remove-JobWindowsRegistration $InstallRoot
    $exe=Join-Path $InstallRoot 'NDIJobConfigurator.exe'
    $sid=Assert-JobDesktopOwner
    $user=([Security.Principal.SecurityIdentifier]$sid).Translate([Security.Principal.NTAccount]).Value
    $action=New-ScheduledTaskAction -Execute $exe -Argument '--lan' -WorkingDirectory $InstallRoot
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName 'NDI Job Configurator Service' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Elevated NDI Job Configurator private LAN web service' -Force | Out-Null
    New-NetFirewallRule -DisplayName 'NDI Job Configurator LAN' -Direction Inbound -Action Allow -Enabled True -Profile Domain,Private -Program $exe -Protocol TCP -LocalPort 8091 -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block | Out-Null
    New-NetFirewallRule -DisplayName 'NDI Job Configurator NDI' -Direction Inbound -Action Allow -Enabled True -Profile Domain,Private -Program $exe -Protocol UDP -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block | Out-Null
    $shell=New-Object -ComObject WScript.Shell
    try {
        foreach($path in Get-JobShortcutPaths) {
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
            $link=$shell.CreateShortcut($path); $link.TargetPath=Join-Path $PSHOME 'powershell.exe'
            $link.Arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $InstallRoot 'Launch-NDIJobConfigurator.ps1') + '"'
            $link.WorkingDirectory=$InstallRoot; $link.WindowStyle=0; $link.IconLocation=(Join-Path $InstallRoot 'NDIJobConfigurator.ico') + ',0'; $link.Save()
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NDIJobConfigurator'
    New-Item $key -Force | Out-Null
    $values=@{DisplayName='NDI Job Configurator'; DisplayVersion=(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion; Publisher='John Lightfoot'; InstallLocation=$InstallRoot; DisplayIcon=(Join-Path $InstallRoot 'NDIJobConfigurator.ico');
        UninstallString=('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallRoot 'Maintenance\Uninstall-NDIJobConfigurator.ps1') + '"')}
    foreach($name in $values.Keys) { New-ItemProperty $key -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null }
    foreach($name in @('NoModify','NoRepair')) { New-ItemProperty $key -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
    Start-ScheduledTask -TaskName 'NDI Job Configurator Service'
}

function Restore-JobWindowsRegistration([string]$InstallRoot, $Snapshot) {
    Remove-JobWindowsRegistration $InstallRoot
    foreach ($task in $Snapshot.tasks) { Register-ScheduledTask -TaskName $task.name -Xml $task.xml -Force | Out-Null }
    $policy=New-Object -ComObject HNetCfg.FwPolicy2
    try {
        foreach ($saved in $Snapshot.rules) {
            $rule=New-Object -ComObject HNetCfg.FWRule
            try {
                $rule.Protocol=$saved.Protocol
                $entries=if($saved -is [hashtable]){$saved.GetEnumerator()}else{$saved.PSObject.Properties | ForEach-Object { @{Key=$_.Name;Value=$_.Value} }}
                foreach($entry in $entries) { if($entry.Key -ne 'Protocol') { $rule.($entry.Key)=$entry.Value } }
                $policy.Rules.Add($rule)
            } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($rule) }
        }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($policy) }
    foreach($path in Get-JobShortcutPaths) {
        $bytes=if($Snapshot.shortcuts -is [hashtable]){$Snapshot.shortcuts[$path]}else{$Snapshot.shortcuts.PSObject.Properties[$path].Value}
        if($bytes) { [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path)); [IO.File]::WriteAllBytes($path,[Convert]::FromBase64String($bytes)) }
    }
    $entries=if($Snapshot.registry -is [hashtable]){$Snapshot.registry.GetEnumerator()}else{$Snapshot.registry.PSObject.Properties | ForEach-Object { @{Key=$_.Name;Value=$_.Value} }}
    if(@($entries).Count -gt 0) {
        $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NDIJobConfigurator'; New-Item $key -Force | Out-Null
        foreach($entry in $entries) { New-ItemProperty $key -Name $entry.Key -Value $entry.Value.value -PropertyType $entry.Value.kind -Force | Out-Null }
    }
    foreach($task in $Snapshot.tasks) { if($task.running) { Start-ScheduledTask -TaskName $task.name } }
}
