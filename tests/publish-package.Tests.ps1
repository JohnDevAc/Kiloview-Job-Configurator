$ErrorActionPreference='Stop'
$publish=Join-Path $PSScriptRoot '..\scripts\Publish.ps1'
$directory=Join-Path ([IO.Path]::GetTempPath()) ('ndi-prebuilt-package-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$archive=Join-Path $directory 'fixture.zip'
[IO.File]::WriteAllText($archive,'Harmless hash-rejection fixture; not an archive.')
function Reject-BeforeBuild([hashtable]$Arguments,[string]$Expected) {
    try { & $publish @Arguments; throw 'Unexpected packaging acceptance.' }
    catch { if ($_.Exception.Message -notlike $Expected) { throw } }
}
Reject-BeforeBuild @{CompanionPackage=$archive} '*requires both*'
Reject-BeforeBuild @{CompanionPackageSha256=('0'*64)} '*requires both*'
Reject-BeforeBuild @{CompanionPackage=$archive;CompanionPackageSha256=('0'*64)} '*does not match*'
Reject-BeforeBuild @{CompanionPackage=$archive;CompanionPackageSha256='not-a-hash'} '*does not match*'
Write-Output 'PASS prebuilt companion requires an explicit valid matching hash before build or output changes'
. (Join-Path $PSScriptRoot '..\scripts\CompanionArchive.ps1')
$payload=Join-Path $directory 'published';[void][IO.Directory]::CreateDirectory($payload)
[IO.File]::WriteAllText((Join-Path $payload 'app.txt'),'independent payload')
function New-FixtureZip([string[]]$Names,[string]$Content='independent payload') {
    $path=Join-Path $directory ([Guid]::NewGuid().ToString('N')+'.zip')
    $zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try { foreach($name in $Names){$entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write($Content)}finally{$writer.Dispose()}} }
    finally {$zip.Dispose()}
    return $path
}
$valid=New-FixtureZip @('app.txt')
Test-CompanionArchive $valid (Get-FileHash -LiteralPath $valid).Hash $payload
foreach($names in @(@('../app.txt'),@('C:/app.txt'),@('app.txt:stream'),@('app.txt','APP.TXT'),@('app.txt','extra.txt'),@('folder/../app.txt'),@('CON.txt'),@('app.txt '),@())) {
    $bad=New-FixtureZip $names
    try { Test-CompanionArchive $bad (Get-FileHash -LiteralPath $bad).Hash $payload;throw 'UNSAFE_ARCHIVE_ACCEPTED' }
    catch { if($_.Exception.Message -eq 'UNSAFE_ARCHIVE_ACCEPTED'){throw} }
}
$bad=New-FixtureZip @('app.txt') 'stale payload'
try { Test-CompanionArchive $bad (Get-FileHash -LiteralPath $bad).Hash $payload;throw 'STALE_ARCHIVE_ACCEPTED' }
catch {if($_.Exception.Message -eq 'STALE_ARCHIVE_ACCEPTED'){throw}}
Write-Output 'PASS companion archive traversal, duplicate, extra, missing and stale payload rejection'
