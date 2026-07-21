param(
    [string]$Repo = "Hoone02/Ticky",
    [string]$InstallDir = "$env:LOCALAPPDATA\Ticky",
    [string]$GitHubToken = $env:TICKY_GITHUB_TOKEN
)

$ErrorActionPreference = "Stop"

function Invoke-GitHubJson($Uri) {
    $headers = @{
        "User-Agent" = "Ticky-Installer"
        "Accept" = "application/vnd.github+json"
    }

    if ($GitHubToken) {
        $headers["Authorization"] = "Bearer $GitHubToken"
    }

    Invoke-RestMethod -Uri $Uri -Headers $headers
}

$release = Invoke-GitHubJson "https://api.github.com/repos/$Repo/releases/latest"
$asset = $release.assets | Where-Object { $_.name -eq "Ticky-win-x64.zip" } | Select-Object -First 1

if (-not $asset) {
    throw "Ticky-win-x64.zip was not found in the latest GitHub release."
}

$tempDir = Join-Path $env:TEMP "TickyInstall"
$zipPath = Join-Path $tempDir "Ticky-win-x64.zip"
$extractDir = Join-Path $tempDir "extract"

New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
if (Test-Path $extractDir) {
    Remove-Item -LiteralPath $extractDir -Recurse -Force
}

$headers = @{
    "User-Agent" = "Ticky-Installer"
}

if ($GitHubToken) {
    $headers["Authorization"] = "Bearer $GitHubToken"
}

Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $zipPath
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $extractDir "*") -Destination $InstallDir -Recurse -Force

$exePath = Join-Path $InstallDir "Ticky.exe"
$startupKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
New-Item -Path $startupKey -Force | Out-Null
Set-ItemProperty -Path $startupKey -Name "Ticky" -Value "`"$exePath`""
Remove-ItemProperty -Path $startupKey -Name "TodoListLight" -ErrorAction SilentlyContinue

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuDir "Ticky.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = $exePath
$shortcut.Save()

Start-Process -FilePath $exePath
Write-Host "Ticky installed to $InstallDir"
