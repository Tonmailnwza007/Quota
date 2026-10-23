# Desktop display contract and transports

Current release contract: **1.0.0**. Canonical providers are codex, codex-cli, antigravity, claude and copilot. There is one Antigravity account/quota entry and no Antigravity shared badge. Earlier App/CLI descriptions below are historical and superseded by this section.

SettingsSnapshot adds antigravitySource=saved/custom/choose. Public SettingsPatch accepts saved/custom only. choose is an unresolved migrated account selection and blocks quota reads until resolved. AdvancedPaths has seven Codex fields plus antigravityAuthPath and antigravityCredentialTarget; removed App/CLI fields are accepted only by read migration, never public patches. Source choice, paths and refresh interval save together. Snapshot persistence can roundtrip choose; unrelated preference updates remain available.

The registered Antigravity source is network-only: saved grant identity verification, context lookup and fresh authoritative quota lookup. No process locator, loopback endpoint, local quota cache or old App binding participates. Credential/source changes invalidate in-flight results on both success and failure paths. A failed refresh exports no old quota. Tokens, session fingerprints, server diagnostics and credential objects stay outside display DTOs.

## Native host (default since 0.1.1)

Windows Forms/WebView2 calls BridgeRuntime directly. The frozen `window.costats` API retains `contracts/index.d.ts`. Native messages use `{id, method, params?}` with correlated `{id, result}` or `{id, error: string}` responses and `{event, data}` notifications. Requests require the local main document and are bounded to 64KB and 64 pending IDs. Unknown methods/fields are rejected. Methods are getState, getSettings, updateSettings, refresh, getRuntimeInfo, hide, openUsage (providerId only), chooseDirectory, testNotification and setStartWithWindows. Smoke-result/window-inspection methods are restricted to isolated smoke mode. There is no public resize/mode API or arbitrary native-object exposure. The private preload sends one invariant `__layout` request of 520 DIPs at document initialization; the host retains fixed360 width, monitor clamping, anchor and popup styles. Folding, loading, settings and provider changes do not change the normal height. No natural-content height observer is registered.

Native RuntimeInfo adds startWithWindows (boolean for the current executable registration; null when Windows registration cannot be read). The optional native-only setStartWithWindows method accepts exactly {enabled:boolean}, returns {enabled:boolean}, and controls only the per-user fmdz.Quota Run value. No executable path or registry key is accepted from the renderer. This preference is read from Windows rather than duplicated in account settings. Fixture/smoke sessions simulate it in memory without touching startup registration. Older hosts omit the method and runtime field.

Normal preferences remain `%LOCALAPPDATA%/quota/settings.json`. The renderer has no credential access, Node integration or HTTP account-data server. The native popup/tool HWND hosts Chromium as a child, avoiding Electron top-level class rules. UI resources are local, protected by CSP, and navigation/downloads/permissions are denied. Provider pages open only through an explicit native allowlist. See native/README.md for lifecycle, isolation and hidden native verification.

## Standalone worker / historical Electron transport v1

The C# worker uses UTF-8 newline-delimited JSON-RPC2.0 on stdin/stdout. Stdout contains protocol messages only. Diagnostic logs go to stderr and never include credential material or raw provider payloads.

Requests have `jsonrpc: "2.0"`, a bounded string `id`, `method`, and optional `params`. Responses use the same `id` and either `result` or `error: {code, message}`. Methods: `getState`, `getSettings`, `refresh`, `updateSettings`, `shutdown`. `updateSettings` params is the SettingsPatch object defined in index.d.ts. `refresh` returns `{accepted: boolean}` immediately; reading sources never blocks the command loop. `shutdown` responds then exits.

Notifications: `ready` with `{protocolVersion:1}`; `stateChanged` with DashboardState. Emit a valid initial empty/loading state; do not pretend the first fetch has completed. Subsequent source updates arrive through notifications.

The wire DTOs follow contracts/index.d.ts in camelCase with string enum names and ISO8601 UTC timestamps. Only the explicit display DTO is serialized. OAuth/access/refresh/ID tokens, raw credential JSON, CSRF tokens and arbitrary provider payloads never cross this boundary. Unknown/non-finite percentages are null; cached input tokens are already included in total input.

The historical Electron host supplies `%LOCALAPPDATA%/quota/settings.json` using `--settings-file`, separate from the original app. The same override isolates tests. A `--fixture` switch uses fully synthetic source objects and never touches real user credentials, original settings or network. It must still exercise the actual protocol and settings persistence path. Only known provider keys, paths, and refresh interval (1..60 minutes) are accepted in patches; reject unknown or structurally invalid fields with a generic invalid-params response.

The worker reuses a vendored source copy of the tested Core/Application/Infrastructure projects. Use a null snapshot writer so the worker never writes the original app's pulse.json. All six providers and discovered Claude profiles must be represented, filtering through the existing orchestrator. Display shared notices only when both matching-account sources are enabled. Antigravity alone may renew the existing saved grant in C#, verify its Google identity and persist the renewed session. Account switching, interactive login and project onboarding are not performed. Tokens and raw credential records remain entirely outside this display protocol.

The Electron main process owns worker lifecycle, validates IPC senders/payloads, and exposes only CostatsApi through the isolated preload. The existing internal costats namespace remains compatible with the reused engine; it is not product branding. Runtime information includes version/connection/hotkey/demo flags, with no window mode or size-changing API. The fixed tray popover starts hidden, skips the taskbar and cannot be maximized/resized. Quota opens usage pages from a provider-ID allowlist, not arbitrary renderer URLs. No public HTTP service, arbitrary shell/FS API, or native Node dependency is required.

## Quota 0.1.4 display and alert preferences

SettingsSnapshot includes showUsageDetails (boolean, default false) and notifications {enabled:true, warningPercent:10, criticalPercent:5}. SettingsPatch supports a boolean showUsageDetails and a partial notifications object; only known keys, booleans and integer percentages with 0 <= critical < warning <= 100 are accepted. Nested settings merge atomically before application and preserve older preferences. Native testNotification takes no params (or an empty object) and returns {accepted:boolean}; content is fixed by the host, with no arbitrary notification/title/message/URL input. Smoke simulates this operation without a real Windows banner. The historical Electron host returns accepted:false for this native-only feature.

The native host evaluates completed display snapshots in C# while the flyout is hidden. Identity/quota history is hash-only in notification-history.json beside the selected settings file. No raw credentials enter alert evaluation. Manual collapsed-account overrides stay in React memory and are absent from persisted preferences and the protocol. Without an override, status ok expands the provider; unavailable statuses collapse it. This derived default follows refreshed detection status, rather than capturing initial loading placeholders.

## Quota 0.1.5 fixed layout

Normal private __layout requests use 520 DIPs. NativeWindow enforces fixed360x520 with monitor work-area/DPI clamping; React uses viewport clamping on small displays. Folding, filtering, loading, empty state and settings never request a shorter window. No public size API is exposed. This supersedes the older content-measurement description.

## Quota 0.1.6 saved sign-in provenance

Antigravity remote readings map the fixed internal saved-sign-in method to sourceLabel="Saved sign-in". Raw login-method strings are not forwarded. This describes how quota is read, with no claim about the interactive client's sign-in state. The CLI card displays "Saved sign-in · CLI login unverified". Antigravity's isSharedAccountQuota renders as "Same quota" and requires both High-confidence measured readings with matching account identity; failed/unknown counterparts clear it. sharedNotice explains the quota/account meaning and does not assert client login. No new protocol fields or credential values are exposed.
