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
