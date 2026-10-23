# Native Quota flyout host

This Windows Forms host embeds the existing React UI in WebView2 and uses `BridgeRuntime` directly. The top-level HWND is a native `WindowsForms...` class with `WS_POPUP` and `WS_EX_TOOLWINDOW`; it has no caption, sizing frame, app-window style, or taskbar button. Chromium remains an embedded child. This avoids matching window-manager rules aimed at Electron's top-level `Chrome_WidgetWin_1` class without changing those rules.

The native executable starts hidden. Its tray icon toggles a compact flyout at the actual tray-click position; `Ctrl+Alt+U` also toggles it. Monitor DPI converts the fixed 360x520-DIP size to physical pixels, clamped to the monitor working area. Folding and provider switches retain that height. Deactivation hides the flyout. React Escape closes settings first, then hides the flyout. Quit unregisters the hotkey, disposes the account runtime and WebView2, then exits.

The Start with Windows switch uses the current user Run key, value fmdz.Quota, with the full quoted quota.exe path. It is opt-in and does not require elevation. It never rewrites startup registration during ordinary launch. Moved portable folders need the switch enabled from the new copy. Synthetic modes simulate the setting in memory. An early tray click is queued until WebView2 is ready; ordinary initialization does not show a warning balloon.

Build from the modern app root with a .NET10 SDK:

```powershell
dotnet build native/Quota.Native.csproj -c Release
```

The project copies the current `web/dist` into an adjacent `renderer` folder, plus `preload.js`, `smoke.js` and `app.ico`. WebView2 SDK is pinned to `1.0.4258.31`. The installed Microsoft Edge WebView2 Runtime is required; the host does not install anything automatically. Missing runtime or assets produce an actionable tray notification. The unused WPF WebView2 assembly is excluded, so this host does not require WPF.

Normal preferences remain at `%LOCALAPPDATA%/quota/settings.json`. WebView2 has its own profile below the same modern app directory. Current sources include two independent Codex accounts and one network-only Antigravity reader; Claude/Copilot are optional. The account runtime starts while the window is hidden and polls independently of visibility or owning clients. Original WPF preferences/snapshots are not written. Antigravity can renew its selected existing saved grant in C# as described in the root README.

The renderer receives only a narrow message API. Native validation requires the local main-document origin and bounded string IDs/64KB frames. Source switches use existing transactional validation. State notifications are marshaled onto the UI thread. All renderer content is served locally under `https://quota.local`; external navigation/popups, downloads and permissions are denied. Local responses include CSP and `nosniff` headers. DevTools, default browser accelerators/context menus, password saving, autofill and host objects are disabled. Usage pages open only through a provider-ID URL allowlist.

Run the hidden integration smoke with the task-local SDK/runtime, or use the apphost when a matching desktop runtime is installed:

```powershell
$env:COSTATS_SMOKE_OUTPUT = 'C:/temp/quota-native-smoke'
dotnet native/bin/Release/net10.0-windows/quota.dll --smoke
```

`--smoke` implies synthetic sources, unique temporary settings/profile, no tray icon, no hotkey registration, no directory dialog, no external browser launch and no visible native window. It runs the root-owned `smoke.js`, then writes `native-smoke.json` to the selected output folder. Success requires both UI assertions and inspected native style/class/visibility checks. There is a 60-second failure timeout. Normal mode rejects the smoke-only result method. `--fixture` is a manually usable synthetic tray session; `--settings-file` overrides preferences outside smoke. Smoke ignores explicit preference paths to preserve isolation.

Historical 0.1.1 verification on the 175% DPI monitor: native class `WindowsForms10.Window.8.app.0.21b46d2_r3_ad1`, style `0x86000000`, extended style `0x00010088`, popup/tool-window true, caption/thick-frame/app-window false, hidden throughout. Physical 665×1120 pixels produced a 381×640 CSS viewport. The hidden smoke passed distinct Plus accounts, real zero versus unknown, both account cards fitting, all six switches/filtering/persistence, all-off clearing, bundled logos and invalid settings/URL rejection. Visible tray, hotkey and folder-dialog interactions were not automated.

Since 0.1.5 the private preload requests invariant 520-DIP height at initialization. NativeWindow enforces 360x520 with work-area/DPI clamping; React also clamps to a short viewport and scrolls content. DWM rounded corners are requested without restoring caption or sizing styles. There is no natural-content height observer or public resize API. These dimensions supersede historical 0.1.1/0.1.2 measurements above.

Current smoke checks the three-provider default, one Antigravity card, all five switches through the real UI, optional Claude profiles/Copilot, folding, alerts, persistence and invariant HWND height. The private __smokeWindow method is smoke-only and absent from the renderer API. Native size assertions use GetWindowRect because a hidden WebView may retain its previous JS viewport. See the current CHECKPOINT.md for packaged results and manual interaction limits.
