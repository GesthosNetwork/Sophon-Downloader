# Sophon Downloader

A lightweight tool for downloading anime game assets using the Sophon-based content delivery system.


## Features

- Full asset download and update download modes
- Uses official Sophon API endpoints (`getBuild`, `getUrl`, etc.)
- Loads available versions from the official API
- Language and region selector
- Built-in live API validation
- Parallel and sequential download modes
- Configurable download threads
- Configurable HTTP connection handles
- Real-time progress logging
- Automatic output directory download
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

### Interactive Menu (Recommended)

Run: `Sophon.Downloader.exe`

You will see:

```
=== Sophon Downloader ===

[1] Full Download
[2] Update Download
[0] Exit
```

Navigate using number keys and follow the prompts.
The application will automatically detect available regions, branches, and versions through the configured API.


## Configuration

The configuration file is automatically generated on first launch.

Example:

```json
{
  "LogLevel": "INFO",
  "DownloadMode": "Parallel",
  "Threads": 6,
  "MaxHttpHandle": 48
}
```

| Parameters | Description |
|---|---|
| `LogLevel` | Minimum logging level (`DEBUG`, `INFO`, `WARNING`, or `ERROR`) |
| `DownloadMode` | Download mode (`Parallel` or `Sequential`) |
| `Threads` | Number of concurrent download workers |
| `MaxHttpHandle` | Maximum number of HTTP connections |


## Download Performance

Download speed depends on your network quality, server response, and configuration.
For unstable connections:

```json
{
  "DownloadMode": "Sequential",
  "Threads": 1,
  "MaxHttpHandle": 8
}
```

For high-speed connections:

```json
{
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
