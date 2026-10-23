using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Usage;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public static class CodexSourceCommon
{
    public static async Task<ProviderReading> ReadAsync(
        ProviderProfile profile,
        CodexAccountRecord? creds,
        string? explicitLogDir,
        CodexOAuthUsageFetcher oauthFetcher,
        UsageLogScanner scanner,
        ExpenseAnalyzer expenseAnalyzer,
        ReadingSource defaultSource,
        CancellationToken cancellationToken,
        string? unconfiguredStatusSummary = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        if (creds is null)
        {
            return new ProviderReading(
                Usage: null,
                Identity: null,
                StatusSummary: unconfiguredStatusSummary ?? $"{profile.DisplayName} login not detected (configure in Settings)",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: defaultSource);
        }

        // Start OAuth call in parallel
        var oauthTask = oauthFetcher.FetchAsync(creds, cancellationToken);

        // ATTRIBUTION GUARD: Only scan logs and calculate expense if we have a reliable AccountId.
        // Unscoped records (e.g. flat API keys or missing account IDs) must NEVER scan shared logs.
        UsageLogResult logResult = default;
        ConsumptionDigest? consumption = null;

        if (!string.IsNullOrWhiteSpace(creds.AccountId))
        {
            logResult = await scanner.ScanCodexAsync(explicitLogDir, creds.AccountId, cancellationToken).ConfigureAwait(false);
            try
            {
                consumption = await expenseAnalyzer.AnalyzeCodexAsync(explicitLogDir, creds.AccountId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                consumption = null;
            }
        }

        var oauthResult = await oauthTask.ConfigureAwait(false);

        var planText = FormatPlanText(oauthResult?.PlanType ?? creds.PlanType);

        // IdentityCard: named arguments ensure Email is in Email, Org is null, Plan is planText, LoginMethod is SourceLabel
        var identity = new IdentityCard(
            ProviderId: profile.ProviderId,
            DisplayName: profile.DisplayName,
            Email: creds.Email,
            Org: null,
            Plan: planText,
            LoginMethod: creds.SourceLabel ?? (defaultSource == ReadingSource.Cli ? "CLI" : "Desktop"));

        if (oauthResult is null && logResult.SessionTokens == 0 && logResult.WeekTokens == 0)
        {
            return new ProviderReading(
                Usage: null,
                Identity: identity,
                StatusSummary: $"No {profile.DisplayName} usage data available",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: defaultSource);
        }

        // Quota windows: ONLY build windows if duration or reset is provided by API; never invent fake resets!
        QuotaWindow? sessionWindow = null;
        if (oauthResult?.PrimaryWindowSeconds is not null)
        {
            sessionWindow = new QuotaWindow(TimeSpan.FromSeconds(oauthResult.PrimaryWindowSeconds.Value), oauthResult.PrimaryResetsAt);
        }
        else if (oauthResult?.PrimaryResetsAt is not null)
        {
            sessionWindow = new QuotaWindow(TimeSpan.Zero, oauthResult.PrimaryResetsAt);
        }

        QuotaWindow? weekWindow = null;
        if (oauthResult?.SecondaryWindowSeconds is not null)
        {
            weekWindow = new QuotaWindow(TimeSpan.FromSeconds(oauthResult.SecondaryWindowSeconds.Value), oauthResult.SecondaryResetsAt);
        }
        else if (oauthResult?.SecondaryResetsAt is not null)
        {
            weekWindow = new QuotaWindow(TimeSpan.Zero, oauthResult.SecondaryResetsAt);
        }

        // Preserve real API percent = 0 as valid known 0
        long? sessionUsed = null;
        long? sessionLimit = null;
        if (oauthResult?.PrimaryUsedPercent is not null)
        {
            sessionUsed = (long)Math.Round(oauthResult.PrimaryUsedPercent.Value);
            sessionLimit = 100;
        }
        else if (logResult.SessionTokens > 0)
        {
            sessionUsed = logResult.SessionTokens;
            sessionLimit = null;
        }

        long? weekUsed = null;
        long? weekLimit = null;
        if (oauthResult?.SecondaryUsedPercent is not null)
        {
            weekUsed = (long)Math.Round(oauthResult.SecondaryUsedPercent.Value);
            weekLimit = 100;
        }
        else if (logResult.WeekTokens > 0)
        {
            weekUsed = logResult.WeekTokens;
            weekLimit = null;
        }

        MonetaryBucket? spendingBucket = null;
        if (oauthResult is { HasCredits: true, CreditBalance: not null } && oauthResult.CreditBalance.Value > 0)
        {
            spendingBucket = MonetaryBucket.ForPrepaidBalance((decimal)oauthResult.CreditBalance.Value);
        }

        var usage = new UsagePulse(
            ProviderId: profile.ProviderId,
            CapturedAt: oauthResult?.FetchedAt ?? logResult.LatestTimestamp ?? now,
            SessionUsed: sessionUsed,
            SessionLimit: sessionLimit,
            WeekUsed: weekUsed,
            WeekLimit: weekLimit,
            SpendingBucket: spendingBucket,
            Consumption: consumption,
            SessionWindow: sessionWindow,
            WeekWindow: weekWindow,
            AccountEmail: creds.Email,
            AccountId: creds.AccountId);

        var statusSummary = oauthResult is not null
            ? $"Updated {FormatRelativeTime(oauthResult.FetchedAt, now)}"
            : (logResult.LatestTimestamp is not null
                ? $"Estimated from logs ({FormatRelativeTime(logResult.LatestTimestamp.Value, now)})"
                : "No usage data");

        var confidence = oauthResult is not null ? ReadingConfidence.High : ReadingConfidence.Medium;
        var source = oauthResult is not null ? ReadingSource.Api : defaultSource;

        return new ProviderReading(
            Usage: usage,
            Identity: identity,
            StatusSummary: statusSummary,
            CapturedAt: usage.CapturedAt,
            Confidence: confidence,
            Source: source);
    }

    public static string? FormatPlanText(string? planType)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return null;
        }

        return char.ToUpper(planType[0]) + planType[1..].ToLower();
    }
}
