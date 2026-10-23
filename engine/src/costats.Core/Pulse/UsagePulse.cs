namespace costats.Core.Pulse;

public sealed record UsagePulse(
    string ProviderId,
    DateTimeOffset CapturedAt,
    long? SessionUsed,
    long? SessionLimit,
    long? WeekUsed,
    long? WeekLimit,
    MonetaryBucket? SpendingBucket,
    ConsumptionDigest? Consumption,
    QuotaWindow? SessionWindow,
    QuotaWindow? WeekWindow,
    IReadOnlyList<NamedQuotaBucket>? QuotaBuckets = null,
    string? AccountEmail = null,
    string? AccountId = null,
    bool IsSharedAccountQuota = false,
    string? StatusMessage = null);
