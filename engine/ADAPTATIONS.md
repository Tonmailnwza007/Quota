# Vendored source provenance

`PROVENANCE.json` records the original SHA256 for every copied file, the upstream repository/base commit and the source branch. The snapshot includes the uncommitted, tested independent-account and Antigravity implementation; replacing it with the base commit would lose those features. Only Core, Application and Infrastructure projects were copied, with their relative hierarchy, central package versions, build properties and license. No credentials, binaries, WPF project or tests from the original checkout were copied. The original checkout is untouched.

`ADAPTATIONS.patch` contains the entire vendored-only delta. `ADAPTED-HASHES.json` records the adapted file hashes. The reader changes are:

- `ClaudeOAuthUsageFetcher`: optional constructor flags preserve upstream defaults while allowing a read-only client to disable delegated owner-client renewal and all legacy disk-cache access. Optional HTTP/renewal/cache-location dependency injection allows hermetic tests of these guards. Request parsing and quota extraction are unchanged.
- `ClaudeLogSource` and `MulticcClaudeLogSource`: optional `readOnly` constructor argument selects those flags. The bridge always passes true.
- `AntigravityQuotaParser` (Quota 0.1.3): hardens grouped/wrapped/direct quota schemas, preserves unknown values, isolates malformed siblings, and safely parses reset/window fields. Research reference: [CodexBar parser](https://github.com/bcharleson/codexbar/blob/3a45dc7cf7c262fd26601fa0edfafddbf5ab465a/Sources/CodexBarCore/Providers/Antigravity/AntigravityQuotaSummaryParser.swift), MIT, copyright 2026 Peter Steinberger; original license retained in the root third-party notices.

This is necessary because the legacy Claude fetcher otherwise invokes `claude /status` on an expired session and writes `%LOCALAPPDATA%/costats/cache`. Read-only bridge mode must do neither. Tests use a fake launcher callback, fake HTTP handler, expired/current synthetic credentials and a temporary cache path to verify no launch, no credential changes and no disk-cache creation.

The original vendored Antigravity App reader still only locates an existing owned process and its loopback quota service. Quota 0.1.3 wraps it with new background readers/session management in `bridge/Antigravity/`; these renew only the existing Antigravity grant and write back verified refreshed credentials. The old Antigravity CLI source is no longer registered. Codex sources still read existing files; Claude remains read-only. Separate `ClaudeCliProbe`/`CodexCliProbe` command-launch classes remain in the copied project but are not registered or invoked by the bridge's selected sources. Null logging and a null pulse-snapshot writer avoid legacy logs and snapshots.

The OAuth installed-application client configuration in the new bridge is functional public client metadata, referenced from crossusage commit `5da900e77293e1b913f13a2a69414906d7223a29` (`plugins/antigravity-cli/plugin.js`) and checked against installed agy 1.4.2. The session implementation is independently written in C#; no user's credential is embedded. The public configuration is selected only when the saved credential identifies that exact client; explicit alternative client configuration must come from the selected credential. Crossusage attribution/license is retained in the root notices.

## Quota 0.1.4 application adaptations

AppSettings adds only Quota display/alert preferences (details hidden, notifications enabled, 10/5 percent remaining defaults). PulseOrchestrator prefers two known stable Antigravity account IDs for shared attribution; otherwise it compares normalized nonempty email. Conflicting IDs never share just because email matches. Existing filtering immediately clears shared flags when a source is hidden. Original checkout files remain untouched; only the vendored engine changes, with regenerated patch/hash provenance. Notification delivery/history lives in the bridge/native layers.

## Quota 0.1.5 account identity correction

AntigravityAppSource leaves AccountId null when the only available identifier is email. The email remains in AccountEmail/Identity.Email for fallback matching; it is not a Google subject ID. This avoids false conflicting-ID results against the verified OAuth subject. Remote quota authority/client/project handling is in bridge/Antigravity, not the vendored engine.

## Quota 0.1.6 measured quota attribution

PulseOrchestrator only applies Antigravity shared-account attribution when both readings have High confidence and at least one finite measured named quota. A retained email on a failed reading, unknown quota, or low-confidence fallback cannot mark its successful counterpart shared. Genuine zero used remains measured. Credential provenance and the distinction from interactive client login are displayed by the bridge/renderer; the engine does not infer a running client's login state.

## Quota 1.0.0-rc.1 unified Antigravity

AppSettings adds canonical AntigravitySource (saved/custom/choose), AntigravityAuthPath and AntigravityCredentialTarget. Original engine properties/defaults remain for compatibility; the modern store maps five providers, disables the legacy CLI provider, and uses opt-in Claude/Copilot defaults for new installations. Migration and source choice are in the bridge. SourceFactory now registers only one direct AntigravityRemoteSource. Neither the legacy App reader, CLI reader, process locator nor App binding file participates in current quota reads. Old reader implementations/tests are retained as compatibility code, not registered sources. The modern mapper exports one Antigravity entry and no Antigravity shared-quota badge. Existing Codex account attribution, filtering and shared notices remain.
