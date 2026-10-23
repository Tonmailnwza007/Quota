# Quota 1.0.0

A minimal Windows tray monitor for AI subscription limits.

- Track two independent Codex accounts using separate App and CLI sign-ins.
- Read one Antigravity account's quota in the background using its saved sign-in, without keeping either Antigravity client open.
- Optionally show Claude profiles and GitHub Copilot; choose visible providers in Settings.
- See remaining allowance and reset times in a fixed-size tray panel with bundled monochrome provider icons.
- Unavailable providers start collapsed. Detected providers start expanded. Click any account arrow to override that default until the app restarts.
- Configure warning and critical notifications using percentages remaining.
- Alert-setting validation and save feedback are announced to screen readers.
- Optionally start hidden in the tray when signing in to Windows.

Extract the entire portable ZIP, quit an older Quota instance, and run `quota.exe`. Microsoft Edge WebView2 Runtime is required; .NET is bundled. Keep the portable folder in place after enabling **Start with Windows**.

Quota uses existing provider sign-ins. Missing, revoked or incompatible credentials require signing in through the owning client. Unknown quota is shown as unavailable. Token counts and estimated API costs are optional and do not represent subscription charges.

Settings from earlier Quota builds are retained. Quota reuses the MIT-licensed costats account engine; attribution and third-party licenses are included.
