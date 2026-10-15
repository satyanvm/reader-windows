# Starts the sync server (server/ in the reader-android repository) on http://127.0.0.1:8787 and
# waits until it answers. Call it in the same CI step as whatever uses it: on Windows the runner
# ends a step's processes when the step ends.
param([string]$ServerDir = "reader-android/server")
$ErrorActionPreference = "Stop"
Push-Location $ServerDir
try {
    if (-not (Test-Path node_modules)) { npm install --no-audit --no-fund | Out-Null }
    $env:READER_DB = Join-Path ([IO.Path]::GetTempPath()) "reader-sync-$([guid]::NewGuid().ToString('N')).sqlite"
    $log = Join-Path ([IO.Path]::GetTempPath()) "reader-sync-server.log"
    Start-Process -FilePath node -ArgumentList "--disable-warning=ExperimentalWarning", "bin/server.js" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden | Out-Null
}
finally {
    Pop-Location
}
for ($i = 0; $i -lt 60; $i++) {
    try { Invoke-RestMethod http://127.0.0.1:8787/healthz | Out-Null; Write-Host "sync server is up"; return }
    catch { Start-Sleep -Milliseconds 500 }
}
Get-Content "$log.err" -ErrorAction SilentlyContinue | Write-Host
throw "the sync server didn't start"
