namespace costats.Core.Pulse;

public sealed record NamedQuotaBucket(
    string Id,
    string Name,
    float? UsedPercent,
    long? UsedUnits = null,
    long? LimitUnits = null,
    DateTimeOffset? ResetsAt = null,
    TimeSpan? WindowDuration = null,
    string? StatusSummary = null)
{
    // Backwards-compatible constructor where Id defaults to Name
    public NamedQuotaBucket(
        string Name,
        float? UsedPercent,
        long? UsedUnits = null,
        long? LimitUnits = null,
        DateTimeOffset? ResetsAt = null,
        TimeSpan? WindowDuration = null,
        string? StatusSummary = null)
        : this(Name, Name, UsedPercent, UsedUnits, LimitUnits, ResetsAt, WindowDuration, StatusSummary)
    {
    }
}
