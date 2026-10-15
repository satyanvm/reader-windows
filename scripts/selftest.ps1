# Runs the real app's self-test (SelfTest.cs) on one book and checks its log.
#   scripts\selftest.ps1 -Book samples\sample.epub -Name epub [-Server http://127.0.0.1:8787]
# Writes <Out>\<Name>\selftest.jsonl and PNG snapshots; fails if the app hangs, logs an error,
# or a snapshot is a single flat colour.
param(
    [Parameter(Mandatory)] [string]$Book,
    [Parameter(Mandatory)] [string]$Name,
    [string]$Step = "all",
    [string]$Server = "",
    [string]$Out = "selftest-out",
    [string]$Exe = "src\Reader.App\bin\Release\net8.0-windows10.0.19041.0\Reader.exe",
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = "Stop"
$dir = Join-Path (Resolve-Path ".") (Join-Path $Out $Name)
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$log = Join-Path $dir "selftest.jsonl"
Remove-Item $log -ErrorAction SilentlyContinue

$env:READER_SUPPORT_DIR = Join-Path $dir "library"
$env:READER_SELFTEST = $Step
$env:READER_SELFTEST_LOG = $log
$env:READER_SELFTEST_OUT = Join-Path $dir "png"
$env:READER_SELFTEST_IMPORT = (Resolve-Path $Book).Path
$env:READER_SELFTEST_EXIT = "1"
$env:READER_SELFTEST_LINGER = "2"
if ($Server) {
    $env:READER_SYNC_BASE_URL = $Server
    $env:READER_SELFTEST_SIGNUP = "selftest-$Name-$([guid]::NewGuid().ToString('N').Substring(0,8))@example.com:correct horse battery"
} else {
    Remove-Item Env:READER_SELFTEST_SIGNUP -ErrorAction SilentlyContinue
}

$p = Start-Process -FilePath $Exe -PassThru
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
    Stop-Process -Id $p.Id -Force
    throw "the app didn't finish the self-test within $TimeoutSeconds s"
}
if (-not (Test-Path $log)) { throw "no self-test log was written" }

$lines = Get-Content $log | ForEach-Object { $_ | ConvertFrom-Json }
$problems = @()
$problems += $lines | Where-Object { $_.PSObject.Properties.Name -contains "error" } | ForEach-Object { "error: $($_.error)" }
$problems += $lines | Where-Object { $_.PSObject.Properties.Name -contains "flat" -and $_.flat } | ForEach-Object { "flat snapshot: $($_.png)" }
$problems += $lines | Where-Object { $_.PSObject.Properties.Name -contains "returnedExactly" -and -not $_.returnedExactly } | ForEach-Object { "back didn't return to the same page ($($_.what))" }
$problems += $lines | Where-Object { $_.trace -like "unhandled:*" } | ForEach-Object { $_.trace }
if (-not ($lines | Where-Object { $_.what -eq "done" })) { $problems += "the self-test never reached the end" }

$lines | Where-Object { $_.PSObject.Properties.Name -notcontains "trace" } | ForEach-Object { $_ | ConvertTo-Json -Compress } | Write-Host
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "::error::$Name: $_" }
    exit 1
}
Write-Host "$Name self-test passed: $((Get-ChildItem (Join-Path $dir 'png') -Filter *.png).Count) snapshots"
