# Development

Use Windows x64, Node 24+, .NET 10 SDK, and Microsoft Edge WebView2 Runtime.

From the repository root:

```powershell
npm.cmd --prefix src/web ci
npm.cmd run lint
npm.cmd test
npm.cmd run build
```

The portable output is `artifacts/1.0.0/quota-win32-x64/`. Packaging refuses to overwrite an existing version folder. Move that folder aside before rebuilding. Set `DOTNET_PATH` to an SDK's `dotnet.exe` when it is not on PATH; `npm test` expects `dotnet` on PATH.

For a browser preview with synthetic accounts, run `npm.cmd run dev`. It does not read real provider credentials.

## Native integration check

```powershell
$env:COSTATS_SMOKE_OUTPUT = "$env:TEMP/quota-native-smoke"
& ./artifacts/1.0.0/quota-win32-x64/quota.exe --smoke
```

Smoke mode stays hidden, uses isolated temporary settings and synthetic sources, and simulates startup and notifications. It checks provider filtering, collapse behavior, fixed panel height, message validation, and native popup styles. Actual Windows sign-in startup and visible notification delivery are manual checks. `--fixture` opens a synthetic tray session for manual inspection.

## Source layout

- `src/web/`: React UI, settings, fixtures, and display adapter.
- `src/native/`: Windows Forms/WebView2 tray host, validated preload, and native tests.
- `src/bridge/`: C# runtime, account/session management, alert logic, and backend tests.
- `src/contracts/`: display types and [message protocol](../src/contracts/PROTOCOL.md).
- `src/engine/`: vendored costats engine, upstream license, provenance, and adaptation records.
- `scripts/`: portable build tooling.

The native host calls the bridge runtime directly. Credentials and raw provider responses never cross the display boundary. The separate bridge console entry point remains available for stdio consumers.

Release builds bundle .NET, omit debug symbols, map developer source paths to a generic directory, and include licenses. Settings, credentials, browser profiles, dependency folders, and generated builds are excluded from Git.
