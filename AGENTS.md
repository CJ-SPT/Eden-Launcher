# Eden Launcher contributor guide

Use C# with four spaces, file-scoped namespaces, System imports first, braces on separate lines, and a 140-character authored-code limit.
Use two spaces for project files, JSON, and XAML. Keep namespaces under the owning project name.
Pin the SDK in global.json. Nullable references and warnings as errors are required.
CSharpier 1.3.0 owns layout; use tools/Format.ps1 and tools/Format.ps1 -Check. Never run full dotnet format.
Keep portable rules and typed models in Eden.Launcher.Core; Windows and Avalonia integration belongs in Eden.Launcher.
Use narrow constructor-injected interfaces. Never persist passwords or log tokens. Keep game files, saves, credentials, and build outputs untracked.
Run tool restore, restore, build --no-restore, test --no-build, then formatting checks.
Use the Computer Use skill to verify changed visible flows. Report automated and live acceptance separately.
Commit verified code, tests, and documentation together, push, and verify the remote commit.
