param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Repo = "Hoone02/Ticky"
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^v') {
    $Version = "v$Version"
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$publishDir = Join-Path $root "publish"
$releaseDir = Join-Path $root "release"
$zipPath = Join-Path $releaseDir "Ticky-win-x64.zip"
$setupPayloadDir = Join-Path $root "setup\Payload"
$setupPayloadPath = Join-Path $setupPayloadDir "Ticky-win-x64.zip"
$installerDir = Join-Path $root "installer-dist"
$installerPath = Join-Path $installerDir "TickySetup.exe"

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
dotnet publish $root -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $publishDir

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
New-Item -ItemType Directory -Force -Path $setupPayloadDir | Out-Null
Copy-Item -LiteralPath $zipPath -Destination $setupPayloadPath -Force

dotnet publish (Join-Path $root "setup\TickySetup.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $installerDir

gh release create $Version $zipPath $installerPath `
    --repo $Repo `
    --title "Ticky $Version" `
    --notes "Ticky $Version release."

Write-Host "Created release $Version with $zipPath and $installerPath"
