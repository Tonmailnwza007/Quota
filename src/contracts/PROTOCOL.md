# Display protocol

Version 1.0.0. Canonical providers: `codex`, `codex-cli`, `antigravity`, `claude`, and `copilot`. Claude profiles use `claude:<profile>`. Antigravity has one account entry. Types are defined in [index.d.ts](index.d.ts).

Only explicit display DTOs and validated preferences cross this boundary. Tokens, credential records, session fingerprints, CSRF values, raw provider responses, and server diagnostics remain inside the C# readers. Unknown or non-finite percentages are null; real zero is preserved.

## Native WebView2 transport

The Windows host calls `BridgeRuntime` directly and exposes a frozen `window.costats` API in the local top-level document only.

- Request: `{id, method, params?}`.
- Response: `{id, result}` or `{id, error: string}`.
- Event: `{event, data}`.

Methods are `getState`, `getSettings`, `updateSettings`, `refresh`, `getRuntimeInfo`, `hide`, `openUsage`, `chooseDirectory`, `testNotification`, and `setStartWithWindows`. Requests are bounded to 64KB and 64 pending IDs. Unknown methods and fields are rejected. Usage pages are selected by provider ID through a host allowlist.

The private preload sends a fixed `__layout` request of 520 DIPs on initialization. The host retains a 360x520-DIP popup with monitor/DPI clamping. There is no public resize, shell, registry, file-reading, or arbitrary native-object API. Inspection/result methods exist only in isolated smoke mode.

`setStartWithWindows` accepts exactly `{enabled:boolean}` and controls only the current executable's per-user `fmdz.Quota` Run value. No executable path or registry key comes from the renderer. Fixture/smoke modes simulate it without registration.

## Preferences and alerts

The native host uses `%LOCALAPPDATA%/quota/settings.json`. Patches are validated before being applied. Changes take effect in memory before ordered atomic disk persistence; save failures return a generic error without exposing raw input.

`antigravitySource` accepts `saved` or `custom` in patches. A migrated unresolved choice may appear as `choose` in snapshots and blocks Antigravity reads until selected. Removed App/CLI preferences are accepted only during migration.

`showUsageDetails` defaults to false. Notification defaults are `{enabled:true, warningPercent:10, criticalPercent:5}`. Thresholds are integers with `0 <= critical < warning <= 100`. `testNotification` accepts no arbitrary content and returns `{accepted:boolean}`.

Alert history stores hashed account/quota keys, bands, and timestamps beside settings. Manual card-collapse overrides stay in React memory. Without an override, successful providers expand and unavailable providers collapse.

## Standalone stdio worker

The optional C# console entry point speaks newline-delimited UTF-8 JSON-RPC 2.0. Stdout contains protocol JSON only; diagnostics go to stderr. Input frames have a 65536-character limit and bounded string IDs. Batch requests and incoming notifications are rejected.

Methods: `getState`, `getSettings`, `refresh`, `updateSettings`, and `shutdown`. Notifications: `ready` with `{protocolVersion:1}`, and `stateChanged` with `DashboardState`. Refresh acknowledges immediately and runs in the background. Initial state is empty/loading with no last-refresh timestamp.

Pass `--settings-file` to select its preferences file. `--fixture` uses isolated synthetic sources and no real credentials or network. Both transports share the same runtime, DTO mapping, and transactional settings validation.
