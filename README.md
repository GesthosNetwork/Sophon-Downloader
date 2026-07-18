# Sophon Downloader

A lightweight tool for downloading anime game assets using the Sophon-based content delivery system.


## Features

- Full asset download and update download modes
- Uses official Sophon API endpoints (`getBuild`, `getUrl`, etc.)
- Language and region selector
- Loads available versions from the official API
- Built-in live API validation
- Parallel and sequential download modes
- Configurable download threads
- Configurable HTTP connection handles
- Real-time progress logging
- Automatic output directory generation
- Lightweight and easy to use
- Useful for users who need specific asset versions for archival, research, testing, or asset management purposes


## Requirements

- Windows x64
- Install <i>[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)</i>


## Compile Instructions

To compile the project:

1. Run `compile.bat`
2. The release output will automatically be generated inside the `bin` folder


## How to Use

### Option 1: Interactive Menu (Recommended)

Run:

```
Sophon.Downloader.exe
```

You will see:

```
=== Sophon Downloader ===

[1] Full Download
[2] Update Download
[0] Exit
```

Navigate using number keys and follow the prompts.

The application will automatically detect available regions, branches, and versions through the configured API.


### Option 2: CLI Usage (Advanced Users)

```cmd
Sophon.Downloader.exe full <gameId> <package> <version> <outputDir> [options]

Sophon.Downloader.exe update <gameId> <package> <fromVersion> <toVersion> <outputDir> [options]
```

## CLI Options

| Option | Description |
|---|---|
| `--region=...` | Region selection (`OSREL` or `CNREL`) |
| `--branch=...` | Branch selection (`main` or `predownload`) |
| `--launcherId=...` | Launcher ID override |
| `--platApp=...` | Platform App ID override |
| `--threads=...` | Number of download workers |
| `--handles=...` | Maximum HTTP connection handles |
| `--downloadMode=...` | Download mode (`Parallel` or `Sequential`) |
| `--LogLevel=...` | Minimum logging level (`DEBUG`, `INFO`, `WARNING`, or `ERROR`) |
| `-h`, `--help` | Show help information |

Example:

### CMD

```bat
Sophon.Downloader.exe full hk4e game 6.5 Downloads\Game_6.5.0 --downloadMode=Sequential

Sophon.Downloader.exe update hk4e game 6.4 6.5 Downloads\Game_6.4.0_6.5.0 --main --threads=8 --handles=128
```

### PowerShell

```powershell
./Sophon.Downloader.exe full hk4e game 6.5 Downloads\Game_6.5.0 --region=CNREL --downloadMode=Parallel

./Sophon.Downloader.exe update hk4e game 6.5 6.6 Downloads\Game_6.5.0_6.6.0 --predownload
```


## config.json

The configuration file is automatically generated on first launch.

Example:

```json
{
  "Region": "CNREL",
  "Branch": "main",
  "LauncherId": "jGHBHlcOq1",
  "PackageId": "8xfMve0uwQ",
  "PlatApp": "ddxf5qt290cg",
  "Password": "CW8GbLNU8f",
  "DownloadMode": "Parallel",
  "Threads": 6,
  "MaxHttpHandle": 48,
  "LogLevel": "INFO"
}
```

### Configuration Options

| Option | Description |
|---|---|
| `Region` | Game region (`OSREL` or `CNREL`) |
| `Branch` | Update branch (`main` or `predownload`) |
| `LauncherId` | Launcher identifier used by the API |
| `PackageId` | Package identifier used by the API |
| `PlatApp` | Platform application identifier |
| `Password` | API authentication parameter |
| `DownloadMode` | Download mode (`Parallel` or `Sequential`) |
| `Threads` | Number of concurrent download workers |
| `MaxHttpHandle` | Maximum number of HTTP connections |
| `LogLevel` | Minimum logging level (`DEBUG`, `INFO`, `WARNING`, or `ERROR`) |


## Download Performance

Download speed depends on network quality, server response, and configuration.

For unstable connections:

```json
{
  ...
  "DownloadMode": "Sequential",
  "Threads": 1,
  "MaxHttpHandle": 8
}
```

For high-speed connections:

```json
{
  ...
  "DownloadMode": "Parallel",
  "Threads": 8,
  "MaxHttpHandle": 64
}
```

Higher values do not always provide better performance.
The optimal configuration depends on network latency, bandwidth, and system resources.


## Notes

- Invalid configuration values automatically fallback to safe defaults.
- Incorrect data types and excessive values are corrected automatically.
- Version availability is validated through live API requests.
- Invalid versions will return a clean error message instead of crashing.
- Maximum thread count is limited based on available CPU resources.


## Disclaimer

This project is an independent open-source tool.
It is not affiliated with any game developer, publisher, or official launcher.
This software is intended for educational, research, archival, testing, and personal asset management purposes only.


## Credits

[Hi3Helper.Sophon](https://github.com/CollapseLauncher/Hi3Helper.Sophon)  
