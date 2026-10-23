using System.Text.Json;
using System.Text.RegularExpressions;
using costats.Core.Pulse;
using Costats.Bridge.Antigravity;

namespace Costats.Bridge;

public sealed record NamedBucketDto(string Id, string Name, double? Percentage, string? ResetAt, string? Detail);
public sealed record ConsumptionDto(long TodayTokens, decimal TodayCostUsd, long MonthTokens, decimal MonthCostUsd, long WeekTokens, decimal WeekCostUsd);
public sealed record SpendingDto(string Kind, decimal Consumed, decimal? Limit, decimal? Remaining, string CurrencySymbol);
public sealed record ProviderDto(string Id, string DisplayName, string Family, string? Plan, string? AccountEmail, string? AccountId,
    string SourceLabel, string Status, string? StatusMessage, bool IsSharedAccountQuota, string? SharedNotice,
    double? SessionProgress, double? WeeklyProgress, string? SessionResetAt, string? WeeklyResetAt,
    NamedBucketDto[] NamedBuckets, string LastUpdated, ConsumptionDto? Consumption, SpendingDto? Spending);
public sealed record DashboardDto(int ProtocolVersion, string Mode, ProviderDto[] Providers, bool IsRefreshing, string? LastRefreshTime, string[] GlobalErrors);

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

public static partial class StateMapper
{
    [GeneratedRegex(@"(?i)(?:bearer\s|(?:access|refresh|id)[_-]?token|csrf|sk-[a-z0-9]|eyJ[a-z0-9_-]{12,})")]
    private static partial Regex SecretPattern();

    // Only these bounded display strings can cross the boundary; source records and diagnostics cannot.
    public static string? DisplayText(string? value, int max = 256)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl) || SecretPattern().IsMatch(value)) return null;
        return value.Trim();
    }

    public static DashboardDto Map(PulseState state, bool fixture) => new(1, fixture ? "demo" : "live",
        state.Providers.Select(p => Map(p.Key, p.Value))
            .OrderBy(p => Rank(p.Id)).ThenByDescending(p => p.Id.StartsWith("claude:") ? p.SessionProgress ?? -1 : -1)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
        state.IsRefreshing, state.IsRefreshing && state.Providers.Count == 0 ? null : Iso(state.LastRefresh),
        state.Errors.Count > 0 ? ["A source could not be refreshed. Check its owning client and try again."] : []);

    public static ProviderDto Map(string id, ProviderReading reading)
    {
        var usage = reading.Usage;
        var family = id.StartsWith("claude:", StringComparison.OrdinalIgnoreCase) ? "claude" : id.Split('-')[0];
        if (family is not ("codex" or "antigravity" or "claude" or "copilot")) family = "other";
        var title = id switch { "codex" => "Codex App", "codex-cli" => "Codex CLI", "antigravity" => "Antigravity", "claude" => "Claude", "copilot" => "GitHub Copilot", _ => family == "claude" ? "Claude · " + (DisplayText(id[7..], 100) ?? "Profile") : "Provider" };
        var source = id switch { "codex" => "Codex App", "codex-cli" => "Codex CLI", "antigravity" => "Antigravity quota service", _ => reading.Source == ReadingSource.LocalLog ? "Local logs" : reading.Source == ReadingSource.Api ? "Account API" : "Local client" };
        if (family == "antigravity" && reading.Identity?.LoginMethod == AntigravityRemoteSource.SavedLoginMethod)
            source = "Saved sign-in";
        var session = Percentage(usage?.SessionUsed, usage?.SessionLimit);
        var weekly = Percentage(usage?.WeekUsed, usage?.WeekLimit);
        var buckets = usage?.QuotaBuckets?.Select(b => new NamedBucketDto(
            DisplayText(b.Id, 160) ?? "quota", DisplayText(b.Name, 160) ?? "Quota",
            Clamp(b.UsedPercent), Iso(b.ResetsAt), null)).ToArray() ?? [];
        var summary = reading.StatusSummary ?? "";
        var status = Status(summary, usage, session, weekly, buckets);
        var message = status switch {
            "expired" => "Session expired. Renew your session in the owning client, then refresh.",
            "offline" => family == "antigravity" ? "Antigravity quota service is unavailable. Check your connection and refresh." : "The owning client is unavailable. Check its connection and refresh.",
            "error" => "Usage could not be read. Check the source settings and your owning client, then refresh.",
            "unknown" => "Quota is unavailable. Check your owning client or configure this source in Settings.",
            _ => reading.Source == ReadingSource.LocalLog ? "Consumption estimated from local logs." : null
        };
        // Only fixed, locally generated errors cross IPC; never forward raw API diagnostics.
        if (family == "antigravity")
        {
            foreach (var failure in Enum.GetValues<AntigravityFailure>())
            {
                var safeMessage = new AntigravitySessionException(failure).Message;
                if (summary != safeMessage) continue;
                status = failure == AntigravityFailure.SignInRequired ? "expired" : failure == AntigravityFailure.Connection ? "offline" : failure is AntigravityFailure.UnverifiedQuota or AntigravityFailure.SourceSelectionRequired ? "unknown" : "error";
                message = safeMessage;
                break;
            }
        }
        var shared = family != "antigravity" && usage?.IsSharedAccountQuota == true && status == "ok";
        var digest = usage?.Consumption;
        ConsumptionDto? consumption = null;
        if (digest is not null)
        {
            var today = DateOnly.FromDateTime(digest.ComputedAt.LocalDateTime);
            var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
            var weekStart = today.AddDays(-daysSinceMonday);
            var week = digest.DailyBreakdown.Where(s => s.Period >= weekStart && s.Period <= today).ToArray();
            consumption = new(Total(digest.TodayTokens), Math.Max(0, digest.TodayCostUsd), Total(digest.RollingWindowTokens),
                Math.Max(0, digest.RollingWindowCostUsd), week.Sum(s => Total(s.Tokens)), week.Sum(s => Math.Max(0, s.ComputedCostUsd)));
        }
        SpendingDto? spending = usage?.SpendingBucket is { } money
            ? new(money.Kind == BucketKind.PrepaidBalance ? "credits" : "overage", Math.Max(0, money.Consumed), money.Ceiling >= 0 ? money.Ceiling : null,
                money.Ceiling >= 0 ? money.Available : null, DisplayText(money.CurrencySymbol, 8) ?? "$") : null;
        // Legacy Claude log-only readers estimate reset times. Those are deliberately not exported as real API resets.
        var resetsKnown = reading.Source != ReadingSource.LocalLog;
        return new(id, title, family, DisplayText(reading.Identity?.Plan), DisplayText(reading.Identity?.Email ?? usage?.AccountEmail),
            DisplayText(usage?.AccountId), source, status, message, shared,
            shared ? "Shared account quota; these sources use the same allowance." : null,
            session, weekly, resetsKnown ? Iso(usage?.SessionWindow?.ResetsAt) : null,
            resetsKnown ? Iso(usage?.WeekWindow?.ResetsAt) : null, buckets, Iso(reading.CapturedAt)!, consumption, spending);
    }

    private static string Status(string summary, UsagePulse? usage, double? session, double? week, NamedBucketDto[] buckets)
    {
        var lower = summary.ToLowerInvariant();
        if (lower.Contains("expired") || lower.Contains("401")) return "expired";
        if (lower.Contains("not running") || lower.Contains("unreachable") || lower.Contains("connection") || lower.Contains("offline")) return "offline";
        if (lower.Contains("error") || lower.Contains("forbidden") || lower.Contains("failed")) return "error";
        return usage is not null && (session is not null || week is not null || buckets.Any(b => b.Percentage is not null)) ? "ok" : "unknown";
    }
    public static double? Percentage(long? used, long? limit) => used is not null && limit > 0 ? Clamp((double)used / limit.Value * 100) : null;
    public static double? Clamp(double? value) => value.HasValue && double.IsFinite(value.Value) ? Math.Clamp(value.Value, 0, 100) : null;
    public static string? Iso(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O");
    private static long Total(TokenLedger tokens) => Math.Max(0, (long)tokens.StandardInput + tokens.CachedInput + tokens.GeneratedOutput + tokens.CacheWriteInput);
    private static int Rank(string id) => id switch { "codex" => 0, "codex-cli" => 1, "antigravity" => 2, "antigravity-cli" => 3, "copilot" => 5, _ => id.StartsWith("claude") ? 4 : 6 };
}
