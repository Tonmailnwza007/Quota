namespace costats.Infrastructure.Providers;

public sealed record CodexAccountRecord(
    string? AccessToken,
    string? RefreshToken,
    string? AccountId,
    string? IdToken,
    string? Email,
    string? PlanType,
    string? SourceLabel)
{
    public override string ToString() =>
        $"CodexAccountRecord {{ AccountId = {AccountId ?? "<null>"}, Email = {Email ?? "<null>"}, PlanType = {PlanType ?? "<null>"}, Source = {SourceLabel ?? "<null>"}, HasAccessToken = {!string.IsNullOrEmpty(AccessToken)}, HasRefreshToken = {!string.IsNullOrEmpty(RefreshToken)}, HasIdToken = {!string.IsNullOrEmpty(IdToken)} }}";
}
