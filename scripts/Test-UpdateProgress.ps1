param(
    [string]$Version = "999.0.0",
    [int]$DelayMs = 120
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "TickyUpdateProgressTest"
$payloadDir = Join-Path $testRoot "payload"
$zipPath = Join-Path $testRoot "Ticky-test-update.zip"

if (Test-Path $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
Set-Content -LiteralPath (Join-Path $payloadDir "update-test.txt") `
    -Encoding UTF8 `
    -Value "Ticky update progress test package. Created $(Get-Date -Format s)."

Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $zipPath -Force

$env:TICKY_UPDATE_TEST_ZIP = $zipPath
$env:TICKY_UPDATE_TEST_VERSION = $Version
$env:TICKY_UPDATE_TEST_DELAY_MS = "$DelayMs"

Write-Host "Starting Ticky with a local test update package."
Write-Host "Click the bottom-right update banner to test the progress gauge."
Write-Host "Package: $zipPath"

dotnet run --project $root
