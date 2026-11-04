# Eden Launcher

Windows desktop launcher for [Eden](https://github.com/CJ-SPT/Eden), written in C# and Avalonia.

The launcher prepares an isolated build 3662168 client, creates standard or invitation-protected developer accounts, signs in, optionally remembers accounts, and launches against local or remote servers. The layout follows the EVE launcher: navigation rail, reserved news column, account cards, game panel, and connection status. Server news delivery is intentionally deferred.

The account header contains **Add account**. Compact cards highlight the selected account, with **Sign out** below the list and launch-group controls together underneath. The wider game panel shows the selected account and running-client count above the full-width **Play** button. Client preparation remains in **Client settings**; the extra preparation shortcut appears only while setup is needed.

## Run

Extract the Windows x64 release ZIP and open `Eden.Desktop.exe`. No separate .NET installation is required for the published build.

1. Open **Settings**, enter the Eden launcher API address, and choose **Connect**. The local default is `http://127.0.0.1:26002`.
2. Select a prepared Eden client containing `tq` and `ResFiles`, then choose **Use this client**. Alternatively, expand **Prepare a new Eden client**, choose the pinned source and an empty destination, and prepare the copy. Other EVE builds are rejected.
3. Choose **Add account**, sign in or create an account with a 6–128 character password, and optionally select **Remember me**. Developer accounts require the server operator's invitation code.
4. Choose the account card and press **Play**. Create characters in the game. Closing the launcher window while games run keeps their gateway connections alive in the system tray. Exit after closing the games.

The server runs separately. See [server setup](docs/server-setup.md) and [acceptance results](docs/acceptance.md).

## Launch groups

In the account column, under **Launch groups**, choose **New group**, give it a name, select its accounts, and save. Choose the saved group and press **Launch group** to start its accounts one after another using the current server profile's verified client. Use **Edit** to rename the group or change its members; **Delete** removes only the group.

Groups are saved separately for each server profile. Add and sign in to each account first; enable **Remember me** to keep group launching available after restarting the launcher. Active sign-ins also work without Remember me during the current launcher session. Groups never store passwords or tokens.

Already-running accounts are skipped. Each account gets its own launch ticket and client settings. An account that needs sign-in or fails to start is listed below the group controls while the other accounts continue. Sign in to any accounts that need attention, then launch the group again; clients already running will be skipped. **Cancel** stops pending launches and leaves clients already started open. The selected account and the individual **Play** button continue to work independently.

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
