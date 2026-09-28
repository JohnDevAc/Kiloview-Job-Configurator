param([ValidateSet('server-main','server-dev')][string]$Name, [switch]$UpdatesOnly)
$ErrorActionPreference = 'Stop'
$candidate = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'server-candidates.json') -Raw | ConvertFrom-Json) | Where-Object { $_.name -eq $Name }
$package = Join-Path $candidate.root 'artifacts\NDIJobConfigurator'
$assembly = Join-Path $candidate.root 'bin\Release\net8.0-windows\win-x64\NDIJobConfigurator.dll'
$isolated = Join-Path ([IO.Path]::GetTempPath()) ('ndi-firmware-release-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $isolated | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$env:NDI_JOB_CONFIGURATOR_DATA_DIR = $isolated
$env:NDI_JOB_CONFIGURATOR_NDI_CONFIG_PATH = Join-Path $isolated 'ndi.json'
$env:NDI_JOB_CONFIGURATOR_SERVICE_PORT = [string]$port
$env:NDI_JOB_CONFIGURATOR_LAN_ACCESS = '0'
$process = Start-Process -FilePath 'dotnet.exe' -ArgumentList @(('"' + $assembly + '"'), '--contentRoot', ('"' + $package + '"')) -WorkingDirectory $package -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $isolated 'stdout.log') -RedirectStandardError (Join-Path $isolated 'stderr.log')
try {
    $base = 'http://127.0.0.1:' + $port
    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw ('Isolated server exited: ' + (Get-Content -LiteralPath (Join-Path $isolated 'stderr.log') -Raw)) }
        try { $health = Invoke-RestMethod -Uri ($base + '/api/health') -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $health -or $health.version -ne $candidate.version) { throw 'Isolated server version/health mismatch' }
    $frontend = Join-Path $isolated 'served-app.js'
    Invoke-WebRequest -UseBasicParsing -Uri ($base + '/app.js') -OutFile $frontend
    if ((Get-FileHash -LiteralPath $frontend).Hash -ne (Get-FileHash -LiteralPath (Join-Path $candidate.root 'wwwroot\app.js')).Hash) { throw 'Served frontend does not match the release source' }
    if ($UpdatesOnly) {
        $updates = foreach ($target in @('server-main','server-dev')) {
            $verified = Get-Content -LiteralPath (Join-Path $PSScriptRoot ($target + '-verification.json')) -Raw | ConvertFrom-Json
            $selection = @{channel=$verified.channel} | ConvertTo-Json -Compress
            $null = Invoke-RestMethod -Uri ($base + '/api/system/update/channel') -Method Put -ContentType 'application/json' -Body $selection
            $update = Invoke-RestMethod -Uri ($base + '/api/system/update') -TimeoutSec 60
            $installer = $verified.assets | Where-Object name -eq 'NDI-Job-Configurator.exe'
            if ($update.latestVersion -ne $verified.version -or $update.sha256 -ne $installer.sha256 -or $update.downloadSizeBytes -ne $installer.size) { throw 'Public updater feed does not match verified installer' }
            $update
        }
        $updates | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'public-updater-verification.json') -Encoding UTF8
        Write-Output 'PASS Main and Development public updater versions, installer sizes and SHA-256 digests'
        return
    }
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    try {
        foreach ($models in @('N6', 'N60', 'N6,N60')) {
            $form = [Net.Http.MultipartFormDataContent]::new()
            try {
                foreach ($model in @('N6', 'N60')) {
                    $selected = $models.Split(',') -contains $model
                    $bytes = [byte[]]@()
                    if ($selected) { $bytes = [byte[]]@(1,2,3) }
                    $part = [Net.Http.ByteArrayContent]::new([byte[]]$bytes)
                    $part.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
                    $part.Headers.ContentDisposition = [Net.Http.Headers.ContentDispositionHeaderValue]::new('form-data')
                    $part.Headers.ContentDisposition.Name = '"' + $model.ToLowerInvariant() + 'Firmware"'
                    $part.Headers.ContentDisposition.FileName = if ($selected) { '"' + $model + '.bin"' } else { '""' }
                    $form.Add($part)
                }
                $response = $client.PostAsync(($base + '/api/firmware/stage?models=' + $models), $form).GetAwaiter().GetResult()
                try {
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    if (-not $response.IsSuccessStatusCode) { throw ('Firmware staging failed: ' + $body) }
                    $staged = $body | ConvertFrom-Json
                    if ($staged.status -ne 'staged' -or ($staged.packages.model -join ',') -ne $models) { throw 'Incorrect HTTP firmware coverage' }
                } finally { $response.Dispose() }
            } finally { $form.Dispose() }
        }
    } finally { $client.Dispose() }
    @{ version=$candidate.version; commit=$candidate.commit; isolatedData=$isolated; health=$health; frontendMatches=$true; httpStagingModels=@('N6','N60','N6,N60'); runtime='Release assembly with packaged frontend'; installed=$false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot ($Name + '-smoke.json')) -Encoding UTF8
    Write-Output ('PASS ' + $Name + ': isolated health, served frontend, N6-only, N60-only and mixed HTTP firmware staging')
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $process.Dispose()
}
