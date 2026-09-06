function Compare-PcAgentVersion([string]$Left, [string]$Right) {
    $leftParts = $Left.Split('+')[0].Split('-', 2)
    $rightParts = $Right.Split('+')[0].Split('-', 2)
    $comparison = ([version]$leftParts[0]).CompareTo([version]$rightParts[0])
    if ($comparison) { return $comparison }
    if ($leftParts.Count -eq 1) { if ($rightParts.Count -eq 1) { return 0 }; return 1 }
    if ($rightParts.Count -eq 1) { return -1 }
    $leftSuffix = $leftParts[1].Split('.'); $rightSuffix = $rightParts[1].Split('.')
    for ($index = 0; $index -lt [Math]::Max($leftSuffix.Count, $rightSuffix.Count); $index++) {
        if ($index -ge $leftSuffix.Count) { return -1 }
        if ($index -ge $rightSuffix.Count) { return 1 }
        [long]$leftNumber = 0; [long]$rightNumber = 0
        $leftNumeric = [long]::TryParse($leftSuffix[$index], [ref]$leftNumber)
        $rightNumeric = [long]::TryParse($rightSuffix[$index], [ref]$rightNumber)
        if ($leftNumeric -and $rightNumeric) { $comparison = $leftNumber.CompareTo($rightNumber) }
        elseif ($leftNumeric) { $comparison = -1 }
        elseif ($rightNumeric) { $comparison = 1 }
        else { $comparison = [StringComparer]::Ordinal.Compare($leftSuffix[$index], $rightSuffix[$index]) }
        if ($comparison) { return $comparison }
    }
    return 0
}

function Test-PcAgentPackage([string]$PackageRoot) {
    $componentRoot = [IO.Path]::GetFullPath((Join-Path $PackageRoot 'pc-onboarding'))
    $manifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'pc-onboarding-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.localCommandSchema -ne 1 -or $manifest.repository -ne 'JohnDevAc/Kiloview-PC-Onboarding') {
        throw 'The bundled PC Agent manifest is incompatible.'
    }
    $seen = @{}
    foreach ($entry in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $componentRoot $entry.path))
        if (-not $path.StartsWith($componentRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            $seen.ContainsKey($path) -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid PC Agent package entry.' }
        $seen[$path] = $true
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "PC Agent package checksum failed: $($entry.path)" }
    }
    foreach ($required in @('NDI Configurator PC Agent Setup.exe', 'Agent\NDI Configurator PC Agent.exe', 'LICENSE.md')) {
        if (-not $seen.ContainsKey([IO.Path]::GetFullPath((Join-Path $componentRoot $required)))) { throw "PC Agent manifest missing $required" }
    }
    return $manifest
}
