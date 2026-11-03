# Eden Launcher

Windows desktop launcher for [Eden](https://github.com/CJ-SPT/Eden), written in C# and Avalonia.

The launcher prepares an isolated build 3662168 client, creates standard or invitation-protected developer accounts, signs in, optionally remembers accounts, and launches against local or remote servers. The layout follows the EVE launcher: navigation rail, reserved news column, account cards, game panel, and connection status. Server news delivery is intentionally deferred.

## Run

Extract the Windows x64 release ZIP and open `Eden.Desktop.exe`. No separate .NET installation is required for the published build.

1. Open **Settings**, enter the Eden launcher API address, and choose **Connect**. The local default is `http://127.0.0.1:26002`.
2. Select a prepared Eden client containing `tq` and `ResFiles`, then choose **Use this client**. Alternatively, expand **Prepare a new Eden client**, choose the pinned source and an empty destination, and prepare the copy. Other EVE builds are rejected.
3. Choose **Add account**, sign in or create an account, and optionally select **Remember me**. Developer accounts require the server operator's invitation code.
4. Choose the account card and press **Play**. Create characters in the game. Closing the launcher window while games run keeps their gateway connections alive in the system tray. Exit after closing the games.

The server runs separately. See [server setup](docs/server-setup.md) and [acceptance results](docs/acceptance.md).

## Build and verify

Install SDK **10.0.403** and Python 3.11 or newer. From this repository:

```powershell
dotnet tool restore
dotnet restore
dotnet build --no-restore
dotnet test --no-build
.\tools\Format.ps1 -Check
```

Apply formatting with `.\tools\Format.ps1`. CSharpier 1.3.0 owns layout; never run full `dotnet format`.

```powershell
.\tools\Publish.ps1
```

This creates `artifacts/Eden-Launcher-win-x64.zip`.

## Structure

- `Eden.Launcher`: Avalonia views, MVVM, native folder pickers, tray integration and Windows credential protection.
- `Eden.Launcher.Core`: typed HTTP contracts, settings, build validation, cancellable preparation, manifest verification, launch tracking and TLS gateway relay.
- `Eden.Launcher.Tests`: core and Avalonia headless tests.

The launcher talks only to the versioned HTTP API. It does not access the server database or load server assemblies. Settings and encrypted remembered tokens live under `%LOCALAPPDATA%\Eden Launcher`. Passwords are never saved. Set `EDEN_LAUNCHER_PROFILE_DIRECTORY` to use an isolated profile directory.

Client downloads, automatic updates, server process management, email and password recovery are outside v1. The news column is reserved for a future server news feed. It contains no hard-coded posts or external news requests.

## Artwork

The background and capsuleer panel use original, unmodified EVE Online images published on [the official EVE website](https://www.eveonline.com/). URLs and SHA-256 checksums are recorded in `Assets/sources.json` in the desktop project. EVE Online artwork remains the property of CCP Games and its rights holders. Eden is an independent project. No generated artwork is included.
