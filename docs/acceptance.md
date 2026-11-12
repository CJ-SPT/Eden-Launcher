# Launcher v1 verification

Automated results, launcher UI checks and native-client observations are recorded separately. The historical native-client checks below were stopped by the user with Escape before final gameplay acceptance.

## Remove signed-out accounts from the launcher

Sign out removes the selected account from the sidebar and saved settings for its server profile. The launcher clears the remembered credentials and saves the removal even if the server cannot be reached.

### Automated validation

Tool restore and offline solution restore passed. The Release build passed with zero warnings and errors in 4.08 seconds. All 29 `ViewModelTests` and `GroupLaunchTests` passed through the xUnit in-process runner in 0.79 seconds. The new regression cases cover local removal, reopening saved settings, and a failed server sign-out. The headless Avalonia window test verifies that the account card is removed from the bound list.

CSharpier, line width, and diff checks passed. The standard test runner could not connect to its test host, the .NET style check could not connect to its build host, and the broader API test run encountered a socket permission error in the loopback relay test.

### Live acceptance

Not performed; agent client testing is disabled.

### Optional maintainer checks

Sign out of one of two accounts in the rebuilt launcher, then reopen it and check the sidebar.

## Signed chat bundle correction — November 5, 2026

Chat trust previously changed a manifest-covered file without updating its signed hash. The client rejected the root manifest and displayed its fallback error, `Couldn't open manifest file bin:/manifest.dat`. The corrected individual/group paths prepare trust in memory, request the matching signed certifi fingerprint and validate both before changing files or starting the game. Only that hash may vary; the rest of build 3662168 remains pinned. Existing affected prepared copies recover on their next corrected launch. Update/restart the server and launcher together.

**Automated validation:** tool/solution restore and build passed with zero warnings/errors (1.5 seconds); all **35 launcher tests passed in 1 second**. Formatting verification (30 files) and diff checks passed. Companion Eden focused chat/launcher/hosting/resident-world checks passed **64 cases in 10 seconds**, with build, both Python policy suites and formatting passing. New regression cases parse the native version-4 manifest entry, compare its hash to the actual prospective bundle, verify its signature, check second-account stability and reject unrelated prefix changes. An older signer that ignores the requested hash is rejected with both existing files unchanged and no client started. Companion Eden validation includes a headless audit of all 219 real native file entries plus the actual signature and repeated development preparation.

**Live acceptance:** Not performed; no visible controls changed and the game was not launched.

**Optional maintainer checks:** Use the rebuilt server/launcher to launch two accounts and exchange Local messages.

## Chat certificate trust — November 5, 2026

The optional `ServerReply.ChatCertificatePem` field is handled by the core launch path. It accepts a current certificate-authority certificate, adds only its public certificate to the prepared copy's `tq/bin64/packages/certifi/cacert.pem`, preserves the existing roots and avoids duplicate installation across account/group launches. A prepared-client marker and existing link/junction checks protect the destination. Old servers omit this field and remain compatible. The change adds no visible controls and does not modify the source EVE installation or operating-system root store.

**Automated validation:** tool/solution restore, build (zero warnings/errors, approximately 0.5 seconds), all 33 launcher tests (approximately 0.7 seconds), and formatting verification passed. New cases verify prepared-copy gating, unchanged-state rejection, preserved original roots, repeated installation, rejection of a non-authority certificate and old-server compatibility.

**Live acceptance:** Not performed; no visible launcher flow changed and the game was not launched. Native chat-window acceptance remains pending under Eden's automated-only policy.

**Optional maintainer checks:** Launch two accounts using the rebuilt launcher/server and exchange Local messages.

## Layout cleanup — November 4, 2026

Narrowed the navigation/news/accounts columns to give the game panel more room. Account cards now use a compact two-line layout with an unobstructed selection indicator; Add account sits in the section header and Sign out is a smaller secondary action. Launch-group creation, selection and actions fit together without scrolling with the two saved accounts. The selected account and running count sit above the centered, full-width Play button. The artwork adapts to available height, and the duplicate client-preparation shortcut hides once a client is ready.

### Automated validation

Tool and solution restores, Release build (zero warnings/errors; 1.30 seconds), all **30 launcher tests** (`dotnet test --no-build -c Release`, 770 ms), formatting verification (.NET style, CSharpier **26 files**, 140-character policy) and diff checks passed. Existing headless tests cover account/launch readiness and group-control bindings. This change adds no gameplay or account-management behavior.

### Live acceptance

Used the Computer Use skill to inspect the rebuilt launcher at its normal **1420 × 820** size and maximized **2560 × 1392** size. Both saved accounts, group selection/actions/status, account context, Play and footer were visible without clipping or an account-column scrollbar. Switching account cards updated the selection and Play context. Opened and closed the existing group editor without saving changes, then restored the original selected account and normal window size. No game client was launched.

### Optional maintainer checks

- Inspect the layout at the minimum window size and other display scaling settings; these were not exercised interactively.

## Launch groups — November 4, 2026

### Automated validation

- Tool restore and solution restore passed. Release build passed with zero warnings or errors (1.73 seconds).
- Formatting verification passed: .NET style, CSharpier, and the Python 140-character policy check (5.40 seconds combined).
- Focused tests: **23 passed**, no failures or skips (803 ms reported test duration). Filter: `FullyQualifiedName~GroupLaunchTests|FullyQualifiedName~ViewModelTests|FullyQualifiedName~ApiAndLaunchTests`, run with `dotnet test --no-build -c Release --filter`.
- Coverage includes creating/editing/deleting groups, settings reopen and older-settings compatibility, server isolation, account-selection preservation, cached and renewed sessions, refresh-token rotation, incorrect account identity, missing/expired credentials, repeated launches, already-running accounts, missing group members, ticket/network/process failures, timeouts, cancellation between accounts and during a ticket request, and Avalonia headless control bindings and readiness gating.
- Group tests use isolated settings and fake APIs/client launchers; no game process is started. No full .NET regression suite was requested or run. The existing preparation and TLS suites were outside this focused test run.

### Live acceptance

Not performed; agent client testing is disabled. Native rendering, input, concurrent game windows, and gameplay feel remain unverified for this change. Historical observations below predate launch groups.

### Optional maintainer checks

- Create a group with two remembered accounts, restart the launcher, and launch it; confirm both native clients open with their own settings.
- Launch the same group again, then cancel a fresh group launch; confirm existing clients stay open and queued clients stop.
- Check the group editor and results at the minimum window size and your normal display scaling.

## Automated

- November 4 password minimum update: the account form now advertises 6–128 characters. Release build, four focused `Eden.Launcher.Tests.ViewModelTests` cases, and formatting checks passed. The registration flow forwards a six-character password. The first Debug build was blocked by the open desktop executable; the successful Release checks used a separate output. No full regression suite or native client acceptance was performed for this update. The server's 21 focused launcher tests cover registration/password-change boundaries, reopen/sign-in and HTTP behavior.
- Server after rebasing onto the committed hardener work, including account/developer/QA compatibility, HTTP contract and throttling tests: 501 passed.
- Launcher: 18 tests passed, including Avalonia headless flows, staging cancellation/resume, manifest tampering, account roles and TLS trust checks.

## Native Windows acceptance

The test server uses an isolated save under `%LOCALAPPDATA%\Eden\Lab\launcher-v1`; the existing gameplay server/save remains separate. The client copy is `E:\EdenDev\Launcher-v1`. The current `E:\Eve` installation is a different build and was correctly rejected. Preparation resumed from the verified build 3662168 source, copied the client/resource cache, and completed verification.

Using the Computer Use skill, connected the launcher to the PC's non-loopback LAN address with HTTPS API port 26202 and gateway TLS port 26201. A short-lived test CA was trusted only inside the launcher process; Windows trust was unchanged. Both endpoints ran on the same Windows PC, so this verifies non-loopback routing and TLS, not a second-machine or Internet deployment.

Created `Launcher UI Developer` through the actual launcher form with an invitation and Remember me enabled. The launcher displayed the developer account card and saved an encrypted refresh token. Adopted the isolated client through Settings, observed Ready to launch, and pressed Play. The server accepted the game connection on TCP 26200, verified build 3662168, completed encrypted login/challenge verification, and sent character-selection data. The native client displayed character creation. The maintainer advanced through creation to the naming step.

Developer character submission exposed the additional `charUnboundMgr.GetQAStarterSystemIDs` call. Pinned ccSvc inspection showed that an empty list skips the optional QA spawn override. Added that response for QA accounts and tests covering QA and ordinary callers. The normal creation request remains ten fields with a None override. Rebuilt the server and restarted the isolated host; Computer Use was then stopped with Escape before exercising the corrected creation flow.

Server output captured during cleanup subsequently recorded successful CreateCharacterWithDoll and selection of the maintainer's pilot `test` (90000003), at station 60015154 with ship 2000000000005. This confirms the corrected developer creation operation and pilot selection server-side. Computer Use remained stopped, so the final station scene was not visually observed by the agent.

Still unverified visually in the final native build: the completed station scene, opening Insider/GM menus, existing Eden Pilot login through the launcher, remembered sign-in after restart, sign-out through the actual window, concurrent clients, and tray/relay behavior across a running game. Automated coverage checks the corresponding account/token, role, preparation, launch-argument, relay and view-model behavior where applicable.

Official artwork was visually inspected in the actual launcher. The promotional left column was then replaced with a reserved empty news section, and Online was removed from Eden branding at the user's request.

## Server skill packages — November 9, 2026

Play and launch groups refresh server metadata, bind launch tickets to the active skill package and download changed archives from the authenticated server origin. Size, SHA-256 and the exact signed native manifest are checked before installation. The launcher preserves the pinned stock archive, verifies previously installed packages against their native signatures, reuses current archives and restores stock when switching to a server without edits. A durable update journal recovers the prior verified archive/manifest after interruption; preparing from an updated source uses its verified originals. Running games block changes to that installation.

**Automated validation:** Tool/solution restore, build with zero warnings/errors, **39 affected tests**, formatting and diff checks passed in **10.26 seconds**. The filter was `FullyQualifiedName~SkillPackageTests|FullyQualifiedName~ChatManifestTests|FullyQualifiedName~GroupLaunchTests|FullyQualifiedName~ViewModelTests|FullyQualifiedName~ApiAndLaunchTests|FullyQualifiedName~InstallationTests`. New cases cover authenticated downloads, exact hashes/sizes, rejected invalid identities and unsigned/tampered archives, installation/server switching/stock restoration, interrupted update recovery and preparation from a patched client. The companion server's 102 affected cases and 11 headless native adapter assertions passed separately.

**Live acceptance:** Not performed; agent client testing is disabled.

**Optional maintainer checks:** Publish a skill package in Eden's web editor and restart the server, then Play and verify the skill; launch another account from a group with that package; close the games and switch to a server without edits to check stock restoration.

## Launcher reliability fixes — November 9, 2026

Fixed five issues found during launcher review:

- Accounts navigation is disabled during active work, and Play keeps its captured server address, client folder and session across awaits.
- Invalid server addresses and malformed replies produce recoverable messages. Group launches report malformed replies per account and continue without exposing response contents.
- Switching to a server without a chat certificate restores the exact pinned original certificate bundle. Recovery also supports earlier launchers' appended certificates and newly prepared copies of those clients. It verifies the original fingerprint before changing files and keeps the older server's request format unchanged.
- Rechecking a client clears readiness before verification; failures and cancellation leave Play disabled until verification succeeds.
- Selecting an account with a rejected remembered sign-in clears its stale credentials and cached session and opens the prefilled sign-in form.

### Automated validation

Tool restore, solution restore, Release build (zero warnings/errors), **62 focused tests**, formatting verification and diff whitespace checks passed in **8.23 seconds** for the final code. This includes **20 new regression cases** covering the five fixes, damaged bundles, unchanged files after rejected tickets, preparation from a chat-enabled source, invalid gateway addresses and continuing group launches after malformed replies.

Development began with the exact filter `FullyQualifiedName=Eden.Launcher.Tests.ViewModelTests.AccountsCannotInterruptPlayAndLaunchKeepsItsCapturedSession`, then exact filters for the other new cases. The final command was:

```powershell
dotnet test tests/Eden.Launcher.Tests/Eden.Launcher.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~ViewModelTests|FullyQualifiedName~ChatManifestTests|FullyQualifiedName~ChatTrustTests|FullyQualifiedName~ApiAndLaunchTests|FullyQualifiedName~GroupLaunchTests|FullyQualifiedName~InstallationTests|FullyQualifiedName~SkillPackageTests"
```

Tests use isolated temporary client files, fake account APIs and Avalonia headless controls. No game process was started. No full .NET regression suite was requested or run.

### Live acceptance

Not performed; agent client testing is disabled. Native input, rendering and successful game startup after switching servers remain unverified interactively.

### Optional maintainer checks

- During a slow Play request, confirm the Accounts button stays disabled and becomes available afterward.
- Switch a previously chat-enabled installation to an older server and confirm native client startup.
- Select an account whose remembered sign-in expired and sign in through the prefilled form.
