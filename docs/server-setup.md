# Eden launcher server setup

Use the Eden server revision containing the `/api/v1` launcher API. Back up the stopped world save before upgrading. The launcher never starts the server for you.

## Local hosting

Start Eden normally. The defaults are game TCP `127.0.0.1:26000`, public gateway HTTP/2 `127.0.0.1:26001`, and launcher HTTP API `127.0.0.1:26002`. The launcher uses the API address; game and gateway addresses come from server metadata.

For the existing seeded accounts, run the server's interactive command in a terminal:

```powershell
dotnet run --project src/Eden.Server -- --set-account-password Eden
dotnet run --project src/Eden.Server -- --set-account-password 'Eden Wing'
```

Passwords are entered twice without echo. No default password is assigned. Account IDs, characters, possessions and progress are preserved. Resetting a password revokes existing launcher sessions.

## Developer accounts

Set `EDEN_DEVELOPER_INVITE_CODE` in the server process environment before starting it. Leave it unset to disable developer registration. Share it only with developers. On the launcher's account creation screen, check **Developer account** and enter the code. The code is neither stored by the launcher nor written to logs.

Build 3662168 developer roles are PLAYER, WORLDMOD, PROGRAMMER, QA, GMH and GML. This enables the client Insider/QA/programmer and GM/WM entry points. Roles are persisted by the server and included in both session initialization messages. Ordinary registration cannot supply arbitrary role bits. Exposing a client tool does not implement its server actions: unsupported slash commands and developer operations remain unavailable.

## Remote hosting

Provide a certificate whose subject alternative names match the advertised gateway/API hostname or IP. Create a server JSON file:

```json
{
  "ServerId": "my-eden-server",
  "Name": "My Eden Server",
  "ListenAddress": "0.0.0.0",
  "GamePort": 26000,
  "GatewayPort": 26001,
  "ApiPort": 26002,
  "GameAddress": "eden.example.com:26000",
  "GatewayAddress": "https://eden.example.com:26001",
  "CertificatePath": "C:\\Eden\\certificates\\server.pfx",
  "DevelopmentLogin": false
}
```

Supply a PFX password through `EDEN_CERTIFICATE_PASSWORD` if needed, then start the server with `--config C:\Eden\server.json`. Combine `--world` with `--config` to select another save. Configure the host/network to allow the three chosen ports. The launcher API address is `https://eden.example.com:26002`.

Publicly trusted certificates use the operating system's normal verification. For a private certificate authority, set `EDEN_LAUNCHER_CA_FILE` to its DER-encoded CA certificate before opening the launcher. This trust applies only to that launcher process. Hostname, validity, chain and server-authentication checks still apply. Windows trust is not modified. Unencrypted remote API/gateway connections and certificate bypasses are not supported.

The native client requires a cleartext loopback gateway override. The launcher relays that connection to the remote gateway over certificate-validated TLS with HTTP/2 ALPN. Keep the launcher running or in its tray while clients are open. Game login continues to use the pinned client's RSA/AES exchange and challenge verification.

## Authentication and compatibility

API v1 provides server metadata, account registration, sessions, refresh, logout and launch tickets. Launcher access tokens last 15 minutes; optional remembered sessions rotate and expire after 30 days. A replayed refresh token revokes its family. Game tickets are single-use and expire after 180 seconds. Token hashes and credentials are stored in additive `Launcher*` tables in the world database, independent of gameplay migration numbering.

The server uses ASP.NET Core's versioned password hasher with 210,000 iterations. Passwords must contain 6–128 characters. Usernames contain 3–32 ASCII letters, digits, spaces, dots, underscores or hyphens and are unique without case distinctions. New accounts have no characters and three available slots.

The API returns no-store responses and throttles registration/sign-in. Signing is restricted to the verified build's exact manifest prefix. Private signing keys remain in the server's Windows key store. Existing development tickets require explicit `--development-login` on both the server and preparation command, with loopback-only hosting.
