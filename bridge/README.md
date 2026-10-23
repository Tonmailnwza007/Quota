# Headless usage bridge

This .NET10 console worker speaks the v1 newline JSON-RPC protocol in `../contracts/PROTOCOL.md`. It has no WPF dependency and exposes no HTTP server. Its only stdout output is protocol JSON; failures emit generic diagnostics to stderr. Only bounded display DTOs cross the boundary. Raw reader/status payloads and credential objects are never serialized.

Build and verify from the modern app root using a .NET10 SDK:

```powershell
dotnet test bridge/tests/Costats.Bridge.Tests.csproj -c Release
dotnet publish bridge/Costats.Bridge.csproj -c Release -r win-x64 --self-contained true -p:RuntimeIdentifiers=win-x64 -o bridge/publish
node bridge/tests/protocol-smoke.mjs bridge/publish/costats-bridge.exe
```

The published `costats-bridge.exe` includes its .NET runtime; end users do not need to install an SDK. Electron owns the child process and should close stdin or send `shutdown` on exit. No shell is required. Send `refresh` to acknowledge immediately and read in the background. Listen for `ready` and `stateChanged`; the first state is empty/loading with a null last-refresh timestamp.

Live mode reuses the vendored Core/Application/Infrastructure source in `../engine`, including all six registered providers and discovered Claude multicc profiles. Copilot is initially disabled, matching the tested engine defaults. The same mutable AppSettings object controls the orchestrator and readers. Source identity, scoped logs and shared-account calculations remain in the tested engine. Sources never combine shared quotas. Profile cards sort worst-first. Unknown quota is null, confirmed zero is 0, and estimated Claude log reset times are omitted from the public DTO. Week cost/token totals are summed from actual daily slices from Monday through the digest's local computed date, matching the UI's “This week” calendar-week label; month totals retain the engine's 30-day rolling window. Costs are API-price estimates, separate from subscription charges.

Modern preferences live only at `%LOCALAPPDATA%/costats-ui/settings.json`. They are **not imported from legacy settings**: first launch uses defaults and source auto-discovery. `--settings-file <path>` overrides this location. Patch validation is all-or-nothing and accepts only the six known provider IDs, ten declared source path/selector fields, and refresh intervals 1..60. Empty paths clear an override. Flags notify the orchestrator before asynchronous, ordered, atomic persistence. A disk error leaves the new preferences active in memory and returns a generic persistence error; it does not block further commands. No legacy settings, snapshots, login/startup preferences or credentials are written. A null snapshot writer replaces the original pulse.json writer.

The vendored Claude reader has a narrow opt-in read-only constructor adaptation, documented in `../engine/ADAPTATIONS.md`. Live bridge construction disables delegated `claude /status` renewal and legacy disk-cache access. Sessions must be renewed in the owning client. Codex and Antigravity auth are read-only; there is no refresh-token rotation, login or account switching. The bridge does not launch native UI.

For isolated synthetic mode:

```powershell
bridge/publish/costats-bridge.exe --fixture --settings-file C:/temp/isolated-costats-settings.json --fixture-delay-ms 1500
```

Fixture mode constructs only synthetic sources and exercises the same orchestrator, DTO mapper, protocol and settings store. Without an explicit file, it uses a unique temporary settings location. It does not inspect credentials, multicc discovery, user settings, process lists or network. Fixture identities end in `.test`. The optional delay (0..30000ms) affects the first synthetic Codex source, allowing responsiveness checks. All automated tests use fixture data, fake transports and isolated temporary files; none perform live account probes.

Frames are UTF-8, one JSON object per line, with a 65536-character input bound and a 128-character string request ID. JSON-RPC batch requests and incoming notifications are not accepted. Unknown fields and duplicate fields are rejected. Settings errors never repeat raw input. stdout writes use one channel/pump, so frames cannot interleave.
