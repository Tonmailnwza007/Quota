# Using Quota

Click the tray icon to toggle the panel, click outside or press Escape to hide it, and use the tray menu to quit. `Ctrl+Alt+U` also toggles the panel when another app has not claimed that shortcut. Check hidden tray icons if Quota is not visible.

## Providers and accounts

New installations show Codex App, Codex CLI, and Antigravity. Enable Claude or GitHub Copilot in Settings. Provider switches apply immediately and save automatically.

- **Codex App** reads `~/.codex/auth.json`, independently of `CODEX_HOME`.
- **Codex CLI** uses an explicit source first, then a distinct `CODEX_HOME`, then one uniquely identifiable different saved Zeron account. Ambiguous accounts require an explicit selection in Advanced settings.
- **Antigravity** uses one saved account for App and CLI usage. Once signed in, neither client needs to stay open. Quota verifies the saved Google identity and requests fresh quota directly. It can renew a compatible existing session; revoked or missing credentials require signing in through the owning client.
- **Claude** supports discovered multicc profiles. Codex and Claude credentials are read-only; renew their sessions through the owning client.

Advanced settings lets you override source paths and the refresh interval. Antigravity normally uses the Windows credential `gemini:antigravity`; custom mode accepts an auth JSON file or credential target. An invalid explicit source never silently substitutes another account.

Quotas show allowance **remaining**. Confirmed zero usage means 100% remaining; unknown readings show `--%`. Failed refreshes clear current quota rather than presenting old values as fresh. Codex shows a Shared badge only when its sources identify the same allowance. Antigravity has one card.

## Layout and details

Each account arrow folds its quota section into the account header. Detected providers start expanded and unavailable providers start collapsed. Your manual choices last until Quota restarts. Folding cards keeps the panel height fixed.

Token totals and estimated API costs are hidden by default. Enable **Advanced settings > Show usage details** to show them. Costs are estimates of API-price equivalents, not subscription bills.

## Notifications

Alerts use percentages remaining. Defaults are 10% warning and 5% critical. Both are editable whole percentages with `0 <= critical < warning <= 100`; save them together with **Save alerts**.

Quota must stay running and online. Disabled providers and unknown, expired, stale, or log-estimated readings do not alert. Notices are deduplicated by account and quota cycle. **Test notification** requests a Windows tray notification; Windows notification and Do not disturb settings control delivery.

## Start with Windows

Enable **Settings > Start with Windows** to launch hidden when you sign in. It is off by default and requires no administrator access. Keep the portable folder in place. After moving or upgrading to another folder, enable the switch from that copy. Turn it off before deleting Quota.

## Local data and privacy

Preferences and notification history live under `%LOCALAPPDATA%/quota/`; the WebView profile is under `webview/` there. The download contains none of these files. Existing valid local preferences are retained when upgrading.

Account credentials stay inside the C# readers. The renderer receives display data and validated preferences through a narrow native message API. There is no account-data HTTP server, credential export, or runtime CDN. Antigravity renews only the selected existing saved grant and does not start a login or switch the client's account.

Provider APIs and saved-session formats may change. Permission errors, outages, incompatible clients, and revoked sessions are reported as unavailable. Quota has no auto-updater.
