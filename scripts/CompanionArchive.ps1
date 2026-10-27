# Read-only admission of the independently published complete Agent payload.
. (Join-Path $PSScriptRoot '..\installer\JobLifecycle.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
function Test-CompanionArchive([string]$Archive,[string]$ExpectedSha256,[string]$PublishedDirectory) {
    $archivePath=Assert-JobSafePath $Archive
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $ExpectedSha256) {
        throw 'The companion archive SHA256 does not match the pinned package.'
    }
    $expected=Get-JobTreeHashes $PublishedDirectory
    if ($expected.Count -eq 0) { throw 'The independently published Agent payload is missing.' }
    $zip=[IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $files=0; [long]$total=0
        foreach($entry in $zip.Entries) {
            $name=$entry.FullName.Replace('\','/')
            $relative=$name.TrimEnd('/')
            $segments=$relative.Split('/')
            if ([string]::IsNullOrWhiteSpace($relative) -or $name.StartsWith('/') -or $relative.Contains(':') -or
                @($segments | Where-Object { $_ -in @('','.', '..') -or $_ -match '[<>"|?*\x00-\x1f]' -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_ -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' }).Count -gt 0 -or
                (($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000 -or -not $seen.Add($relative)) {
                throw "Unsafe or duplicate companion archive entry: $($entry.FullName)"
            }
            if ($name.EndsWith('/')) { continue }
            $files++; $total+=$entry.Length
            if ($files -gt 4096 -or $entry.Length -gt 512MB -or $total -gt 1GB) { throw 'Companion archive exceeds the complete package limits.' }
            $key=$relative.Replace('/','\')
            if (-not $expected.ContainsKey($key)) { throw "Unexpected companion payload file: $relative" }
            $stream=$entry.Open(); $hasher=[Security.Cryptography.SHA256]::Create()
            try { $hash=([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-','') }
            finally { $stream.Dispose();$hasher.Dispose() }
            if ($hash -ne $expected[$key]) { throw "Companion archive differs from its independently published payload: $relative" }
        }
        if ($files -ne $expected.Count) { throw 'Companion archive is missing independently published payload files.' }
    } finally { $zip.Dispose() }
}
