# Ticky

Ticky is a lightweight Windows todo app built with WPF.

## Features

- Add, check, uncheck, delete, and reorder todo items
- Add removable separators with optional titles
- Always-on-top pin mode
- Adjustable transparency
- Automatic startup registration
- Persistent local state

## Build

```powershell
dotnet build
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o publish
```

The published app is created under `publish/Ticky.exe`.

## Installer

Download `TickySetup.exe` from GitHub Releases and run it.

The setup executable installs Ticky to `%LOCALAPPDATA%\Ticky`, creates a Start Menu shortcut, registers startup, and launches the app.

PowerShell script installation is also available:

```powershell
powershell -ExecutionPolicy Bypass -File installer/Install-Ticky.ps1
```

The installer downloads `Ticky-win-x64.zip` from the latest GitHub release, installs it to `%LOCALAPPDATA%\Ticky`, creates a Start Menu shortcut, and registers startup.

## Release

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Create-Release.ps1 -Version 0.1.0
```

The app updater also reads the latest GitHub release and downloads `Ticky-win-x64.zip`.

For private repositories, set `TICKY_GITHUB_TOKEN` before installing or updating. Public repositories do not need a token.
