param([ValidateSet('server-main','server-dev')][string]$Name, [switch]$Draft)
$ErrorActionPreference = 'Stop'
$local = Get-Content -LiteralPath (Join-Path $PSScriptRoot ($Name + '-verification.json')) -Raw | ConvertFrom-Json
$releasePath = 'repos/JohnDevAc/Kiloview-Job-Configurator/releases/tags/v' + $local.version
if ($Draft) {
    $listingJson = & gh api 'repos/JohnDevAc/Kiloview-Job-Configurator/releases?per_page=20'
    if ($LASTEXITCODE -ne 0) { throw 'Unable to find draft release' }
    $matching = @(($listingJson | ConvertFrom-Json) | Where-Object tag_name -eq ('v' + $local.version))
    if ($matching.Count -ne 1) { throw 'Expected one matching draft release' }
    $releasePath = 'repos/JohnDevAc/Kiloview-Job-Configurator/releases/' + $matching[0].id
}
$remoteJson = & gh api $releasePath
if ($LASTEXITCODE -ne 0) { throw 'Unable to read uploaded release' }
$remote = $remoteJson | ConvertFrom-Json
$branch = if ($Name -eq 'server-main') { 'main' } else { 'development' }
if ($remote.tag_name -ne ('v' + $local.version) -or $remote.target_commitish -ne $branch -or $remote.draft -ne [bool]$Draft -or $remote.prerelease -ne ($Name -eq 'server-dev')) { throw 'Release tag, channel, target or visibility mismatch' }
if ($remote.assets.Count -ne $local.assets.Count) { throw 'Release asset count mismatch' }
foreach ($asset in $local.assets) {
    $published = @($remote.assets | Where-Object name -eq $asset.name)
    if ($published.Count -ne 1 -or $published[0].state -ne 'uploaded' -or $published[0].size -ne $asset.size -or $published[0].digest -ne ('sha256:' + $asset.sha256)) { throw ('Release asset size/hash mismatch: ' + $asset.name) }
}
$tagJson = & gh api ('repos/JohnDevAc/Kiloview-Job-Configurator/git/ref/tags/v' + $local.version)
if ($LASTEXITCODE -ne 0) { throw 'Unable to verify release tag' }
$tag = $tagJson | ConvertFrom-Json
if ($tag.object.type -ne 'commit' -or $tag.object.sha -ne $local.commit) { throw 'Published tag differs from verified source commit' }
$remoteJson | Set-Content -LiteralPath (Join-Path $PSScriptRoot ($Name + '-remote-release.json')) -Encoding UTF8
@{ tag=$remote.tag_name; commit=$tag.object.sha; url=$remote.html_url; target=$branch; draft=$remote.draft; prerelease=$remote.prerelease; allAssetsVerified=$true; assets=$remote.assets | Select-Object name,size,digest } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot ($Name + '-remote-verification.json')) -Encoding UTF8
Write-Output ('PASS ' + $remote.tag_name + ': remote tag, channel, target and all four asset sizes/SHA-256 digests')
