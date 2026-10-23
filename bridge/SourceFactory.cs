using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using costats.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Costats.Bridge.Antigravity;

namespace Costats.Bridge;

public static class SourceFactory
{
    public static IReadOnlyList<ISignalSource> CreateLive(AppSettings settings, string? stateDirectory = null)
    {
        var sources = new List<ISignalSource>
        {
            new CodexLogSource(settings), new CodexCliSource(settings),
            CreateAntigravity(settings),
            new CopilotPersonalSource(settings, new CredentialVault(), new CopilotUsageFetcher(NullLogger<CopilotUsageFetcher>.Instance), NullLogger<CopilotPersonalSource>.Instance)
        };
        var discovery = new MulticcDiscoveryService(new MulticcConfigReader(NullLogger<MulticcConfigReader>.Instance), settings.MulticcConfigPath);
        if (settings.MulticcEnabled && discovery.IsDetected && discovery.Profiles.Count > 0)
        {
            var profiles = discovery.Profiles.Where(p => settings.MulticcSelectedProfile is null || p.Name.Equals(settings.MulticcSelectedProfile, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (profiles.Length > 0) sources.AddRange(profiles.Select(p => new MulticcClaudeLogSource(p, readOnly: true)));
            else sources.Add(new ClaudeLogSource(readOnly: true));
        }
        else sources.Add(new ClaudeLogSource(readOnly: true));
        return sources;
    }

    // One account and one network-only reader. No process detection, local quota cache,
    // previous App binding or owner-client lifetime participates in a quota refresh.
    public static AntigravityRemoteSource CreateAntigravity(AppSettings settings,
        IAntigravityCredentialStore? credentialStore = null, HttpClient? httpClient = null)
    {
        var http = httpClient ?? AntigravitySessionManager.CreateHttpClient();
        var sessions = new AntigravitySessionManager(credentialStore ?? new WindowsAntigravityCredentialStore(), http);
        return new(ProviderCatalog.Antigravity, sessions, () => AntigravityCredentialLocation.ForSettings(settings), http);
    }

    public static IReadOnlyList<ISignalSource> CreateFixtures(int slowMilliseconds = 0) =>
    [
        new FixtureSource("codex", "alpha@example.test", "fixture-alpha", 34, 52, slowMilliseconds),
        new FixtureSource("codex-cli", "beta@example.test", "fixture-beta", 0, null),
        new FixtureSource("antigravity", "google@example.test", "fixture-google", 65, null),
        new FixtureSource("claude:research", "research@example.test", null, 20, 35),
        new FixtureSource("claude:work", "work@example.test", null, 94, 78),
        new FixtureSource("copilot", "copilot@example.test", null, null, null)
    ];
}

public sealed class FixtureSource(string id, string email, string? accountId, long? session, long? week, int delayMilliseconds = 0, string? summary = null) : ISignalSource
{
    public ProviderProfile Profile { get; } = new(id, id, "#00F0FF");
    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var tokens = new TokenLedger { StandardInput = 1000, CachedInput = 250, GeneratedOutput = 500 };
        var consumption = new ConsumptionDigest
        {
            TodayTokens = tokens, TodayCostUsd = 0.05m, RollingWindowTokens = tokens, RollingWindowCostUsd = 0.05m, RollingWindowDays = 30,
            DailyBreakdown = [new() { Period = DateOnly.FromDateTime(now.LocalDateTime), ModelIdentifier = "fixture", Tokens = tokens, ComputedCostUsd = 0.05m }], ComputedAt = now
        };
        var usage = new UsagePulse(id, now, session, session is not null ? 100 : null, week, week is not null ? 100 : null,
            null, consumption, session is not null ? new(TimeSpan.FromHours(5), now.AddHours(3)) : null,
            week is not null ? new(TimeSpan.FromDays(7), now.AddDays(3)) : null,
            id.StartsWith("antigravity") ? [new NamedQuotaBucket("fixture-model", "Model quota", session is not null ? (float)session : null)] : [], email, accountId);
        return new(usage, new(id, id, email, null, id.StartsWith("codex") ? "Plus" : "Pro", "Synthetic fixture"), summary ?? "Updated", now, ReadingConfidence.High, ReadingSource.Api);
    }
}
