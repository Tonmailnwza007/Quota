using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;

namespace Costats.Bridge.Antigravity;

public sealed class AntigravityRemoteSource : ISignalSource
{
    // A valid saved grant proves the quota account, not an interactive client's login state.
    public const string SavedLoginMethod = "Saved Antigravity sign-in (background)";
    private readonly AntigravitySessionManager _sessions;
    private readonly Func<AntigravityCredentialLocation> _location;
    private readonly HttpClient _http;
    private readonly Func<string, bool>? _accountGuard;
    public ProviderProfile Profile { get; }
    public AntigravityRemoteSource(ProviderProfile profile, AntigravitySessionManager sessions, Func<AntigravityCredentialLocation> location,
        HttpClient httpClient, Func<string, bool>? accountGuard = null)
        => (Profile, _sessions, _location, _http, _accountGuard) = (profile, sessions, location, httpClient, accountGuard);

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        AntigravityCredentials? identity = null;
        AntigravityCredentialLocation? location = null;
        AntigravitySession? session = null;
        try
        {
            location = _location();
            session = await _sessions.AcquireAsync(location, deadline.Token).ConfigureAwait(false);
            identity = session.Credentials;
            if (_accountGuard is not null && !_accountGuard(identity.Email!)) throw new AntigravitySessionException(AntigravityFailure.IdentityMismatch);
            var fetched = await FetchQuotaAsync(identity.AccessToken, deadline.Token).ConfigureAwait(false);
            if (fetched.Unauthorized && !session.WasRefreshed)
            {
                session = await _sessions.AcquireAsync(location, deadline.Token, identity.AccessToken).ConfigureAwait(false);
                identity = session.Credentials;
                if (_accountGuard is not null && !_accountGuard(identity.Email!)) throw new AntigravitySessionException(AntigravityFailure.IdentityMismatch);
                fetched = await FetchQuotaAsync(identity.AccessToken, deadline.Token).ConfigureAwait(false);
            }
            if (fetched.Unauthorized) throw new AntigravitySessionException(AntigravityFailure.SignInRequired);
            // A sign-out, source change or account switch while HTTP was in flight invalidates this reading.
            if (_location().Key != location.Key || !_sessions.IsCurrent(location, session))
                throw new AntigravitySessionException(AntigravityFailure.CredentialChanged);
            var now = DateTimeOffset.UtcNow;
            var measured = fetched.Buckets.Any(bucket => bucket.UsedPercent is not null);
            var usage = new UsagePulse(Profile.ProviderId, now, null, null, null, null, null, null, null, null,
                fetched.Buckets, identity.Email, identity.Subject);
            return new(usage, new(Profile.ProviderId, Profile.DisplayName, identity.Email, null, fetched.Plan,
                SavedLoginMethod),
                measured ? "Live (background quota)" : "Antigravity limits not available", now,
                measured ? ReadingConfidence.High : ReadingConfidence.Low, ReadingSource.Api);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Fail(AntigravityFailure.Connection); }
        catch (AntigravitySessionException exception) { return Fail(exception.Failure); }
        catch (HttpRequestException) { return Fail(AntigravityFailure.Connection); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail(AntigravityFailure.Storage); }
        catch { return Fail(AntigravityFailure.InvalidResponse); }

        ProviderReading Fail(AntigravityFailure failure)
        {
            if (failure is AntigravityFailure.CredentialChanged or AntigravityFailure.SourceSelectionRequired)
                return Failure(failure, null);
            if (identity is not null && location is not null && session is not null)
            {
                try
                {
                    if (_location().Key != location.Key || !_sessions.IsCurrent(location, session))
                        return Failure(AntigravityFailure.CredentialChanged, null);
                }
                catch { identity = null; } // An unverifiable previous identity cannot be republished.
            }
            return Failure(failure, identity);
        }
    }
    private ProviderReading Failure(AntigravityFailure failure, AntigravityCredentials? identity) => new(null,
        identity is null ? null : new(Profile.ProviderId, Profile.DisplayName, identity.Email, null, null, SavedLoginMethod),
        new AntigravitySessionException(failure).Message, DateTimeOffset.UtcNow, ReadingConfidence.Low, ReadingSource.Api);

    private sealed record QuotaFetch(IReadOnlyList<NamedQuotaBucket> Buckets, string? Plan = null, bool Unauthorized = false);
    public const string OfficialQuotaHost = "daily-cloudcode-pa.googleapis.com";
    // Compatibility request identity verified against agy 1.4.2 and the local app on 2026-10-19.
    // The generic/cloudcode route can return numeric full-quota placeholders for this same account.
    public const string QuotaUserAgent = "antigravity/cli/1.4.2 windows/amd64";
    private async Task<QuotaFetch> FetchQuotaAsync(string access, CancellationToken cancellationToken)
    {
        var (loadCode, loadPayload) = await PostAsync(OfficialQuotaHost, "loadCodeAssist",
            "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}", access, cancellationToken).ConfigureAwait(false);
        if (loadCode == HttpStatusCode.Unauthorized) return new([], Unauthorized: true);
        RequireSuccess(loadCode);
        using var load = JsonDocument.Parse(loadPayload);
        var root = load.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AntigravitySessionException(AntigravityFailure.InvalidResponse);
        var project = AntigravitySessionManager.Text(root, "cloudaicompanionProject");
        if (project is null && root.TryGetProperty("cloudaicompanionProject", out var reference))
            project = AntigravitySessionManager.Text(reference, "id") ?? AntigravitySessionManager.Text(reference, "projectId");
        project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        string? plan = null;
        if (root.TryGetProperty("paidTier", out var paid)) plan = AntigravitySessionManager.Text(paid, "name");
        var hasTier = root.TryGetProperty("currentTier", out var tier) && tier.ValueKind == JsonValueKind.Object &&
            (!string.IsNullOrWhiteSpace(AntigravitySessionManager.Text(tier, "id")) || !string.IsNullOrWhiteSpace(AntigravitySessionManager.Text(tier, "name")));
        if (plan is null && hasTier) plan = AntigravitySessionManager.Text(tier, "name");
        if (project is null && !hasTier)
        {
            var rejected = root.TryGetProperty("ineligibleTiers", out var ineligible) && ineligible.ValueKind == JsonValueKind.Array &&
                ineligible.EnumerateArray().Any(value => AntigravitySessionManager.Text(value, "reasonCode") == "GOOGLE_TOS_NOT_SUPPORTED_BY_CLIENT");
            throw new AntigravitySessionException(rejected ? AntigravityFailure.ClientMismatch : AntigravityFailure.UnverifiedQuota);
        }
        // Resolve the account context before reading quotas. Never provision/onboard a project here.
        var body = project is null ? "{}" : JsonSerializer.Serialize(new {project});
        var (code, payload) = await PostAsync(OfficialQuotaHost, "retrieveUserQuotaSummary", body, access, cancellationToken).ConfigureAwait(false);
        if (code == HttpStatusCode.Unauthorized) return new([], Unauthorized: true);
        RequireSuccess(code);
        var parsed = AntigravityQuotaParser.ParseQuotaSummary(payload);
        return new(parsed.Buckets, plan);
    }
    private static void RequireSuccess(HttpStatusCode code)
    {
        if (code == HttpStatusCode.Forbidden) throw new AntigravitySessionException(AntigravityFailure.Forbidden);
        if (code == HttpStatusCode.TooManyRequests || (int)code >= 500) throw new AntigravitySessionException(AntigravityFailure.Connection);
        if ((int)code is < 200 or >= 300) throw new AntigravitySessionException(AntigravityFailure.UnverifiedQuota);
    }
    private async Task<(HttpStatusCode Code, string Payload)> PostAsync(string host, string method, string body, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/v1internal:{method}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("User-Agent", QuotaUserAgent);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : "{}");
    }
}

public interface IAntigravityAccountBinding
{
    void SetFromLocal(string email);
    bool MatchOrBindRemote(string email);
}

public sealed class AntigravityAccountBinding(string path) : IAntigravityAccountBinding
{
    private readonly object _lock = new();
    private string? _hash;
    private bool _loaded;
    private static string Hash(string email) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
    public void SetFromLocal(string email)
    {
        lock (_lock)
        {
            Load();
            var candidate = Hash(email);
            if (_hash != candidate) Save(candidate);
        }
    }
    public bool MatchOrBindRemote(string email)
    {
        lock (_lock)
        {
            Load();
            var candidate = Hash(email);
            if (string.IsNullOrEmpty(_hash)) { Save(candidate); return true; }
            return _hash == candidate;
        }
    }
    private void Load()
    {
        if (_loaded) return;
        _hash = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        _loaded = true;
    }
    private void Save(string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, hash, Encoding.UTF8); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _hash = hash; _loaded = true;
    }
}

public sealed class AntigravityAppWithBackgroundSource(ISignalSource local, ISignalSource background, IAntigravityAccountBinding binding) : ISignalSource
{
    public ProviderProfile Profile => ProviderCatalog.Antigravity;
    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        ProviderReading? reading = null;
        try { reading = await local.ReadAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* Local process/API failure must not prevent the saved-session fallback. */ }
        if (!string.IsNullOrWhiteSpace(reading?.Identity?.Email))
        {
            try { binding.SetFromLocal(reading.Identity.Email); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new(null, reading.Identity, new AntigravitySessionException(AntigravityFailure.Storage).Message,
                    DateTimeOffset.UtcNow, ReadingConfidence.Low, ReadingSource.Api);
            }
        }
        if (reading?.Usage is { } localUsage && !string.IsNullOrWhiteSpace(reading.Identity?.Email) &&
            string.Equals(localUsage.AccountId, reading.Identity.Email, StringComparison.OrdinalIgnoreCase))
            reading = reading with { Usage = localUsage with { AccountId = null } };
        ProviderReading remote;
        try { remote = await background.ReadAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            remote = new(null, null, new AntigravitySessionException(AntigravityFailure.Connection).Message,
                DateTimeOffset.UtcNow, ReadingConfidence.Low, ReadingSource.Api);
        }
        if (!string.IsNullOrWhiteSpace(reading?.Identity?.Email) && !string.IsNullOrWhiteSpace(remote.Identity?.Email) &&
            !string.Equals(reading.Identity.Email.Trim(), remote.Identity.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            remote = new(null, null, new AntigravitySessionException(AntigravityFailure.IdentityMismatch).Message,
                DateTimeOffset.UtcNow, ReadingConfidence.Low, ReadingSource.Api);
        if (remote.Usage?.QuotaBuckets?.Any(bucket => bucket.UsedPercent is not null) == true) return remote;
        if (reading?.Usage?.QuotaBuckets?.Any(bucket => bucket.UsedPercent is not null) == true) return reading;
        return remote;
    }
}
