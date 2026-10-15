# Builds a self-contained Reader.exe for 64-bit Windows (no .NET install needed on the target).
#   scripts\build.ps1                       -> publish\win-x64\Reader.exe
#   scripts\build.ps1 -GoogleClientId <id>  -> with Continue with Google
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$GoogleClientId = $env:READER_GOOGLE_CLIENT_ID,
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipTests) {
        dotnet test tests/Reader.Core.Tests -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "tests failed" }
    }
    $out = Join-Path $root "publish/$Runtime"
    dotnet publish src/Reader.App/Reader.App.csproj -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:ReaderGoogleClientId="$GoogleClientId" -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Write-Host "Built $(Join-Path $out 'Reader.exe')"
}
finally {
    Pop-Location
}
