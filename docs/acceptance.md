# Launcher v1 verification

Automated results and native-client observations are recorded separately. Computer Use was stopped by the user with Escape before the final gameplay checks.

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
