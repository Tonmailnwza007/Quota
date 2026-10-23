using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Security;

namespace costats.Infrastructure.Providers;

public sealed record AntigravityCliCredential(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    string? Email,
    string? Subject,
    string SourceLabel)
{
    public override string ToString() =>
        $"AntigravityCliCredential {{ Source = '{SourceLabel}', Email = '{Email}', HasAccessToken = {!string.IsNullOrEmpty(AccessToken)}, HasRefreshToken = {!string.IsNullOrEmpty(RefreshToken)}, HasIdToken = {!string.IsNullOrEmpty(IdToken)} }}";
}

public interface IAntigravityCliCredentialReader
{
    AntigravityCliCredential? ReadCredential(AppSettings? settings);
}

public sealed class WindowsAntigravityCliCredentialReader : IAntigravityCliCredentialReader
{
    public const string DefaultCredentialTarget = "gemini:antigravity";

    public AntigravityCliCredential? ReadCredential(AppSettings? settings)
    {
        string? json = null;
        string sourceLabel = "Windows Credential Manager (gemini:antigravity)";

        if (!string.IsNullOrWhiteSpace(settings?.AntigravityCliAuthPath))
        {
            var path = settings.AntigravityCliAuthPath.Trim();
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                json = File.ReadAllText(path);
                sourceLabel = $"Auth File ({Path.GetFileName(path)})";
            }
            catch
            {
                return null;
            }
        }
        else
        {
            var target = !string.IsNullOrWhiteSpace(settings?.AntigravityCliCredentialTarget)
                ? settings.AntigravityCliCredentialTarget.Trim()
                : DefaultCredentialTarget;

            json = WindowsCredentialHelper.ReadGenericCredential(target);
            sourceLabel = $"Windows Credential ({target})";
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return ParseCredentialJson(json, sourceLabel);
    }

    public static AntigravityCliCredential? ParseCredentialJson(string json, string sourceLabel)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? accessToken = null;
            string? refreshToken = null;
            string? idToken = null;

            if (root.TryGetProperty("token", out var tokenObj) && tokenObj.ValueKind == JsonValueKind.Object)
            {
                accessToken = tokenObj.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                refreshToken = tokenObj.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            }
            else if (root.TryGetProperty("access_token", out var directAt))
            {
                accessToken = directAt.GetString();
                refreshToken = root.TryGetProperty("refresh_token", out var directRt) ? directRt.GetString() : null;
            }

            if (root.TryGetProperty("id_token", out var idt))
            {
                idToken = idt.GetString();
            }

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            string? email = null;
            string? subject = null;

            if (!string.IsNullOrWhiteSpace(idToken))
            {
                var claims = ParseJwtClaims(idToken);
                email = claims.Email;
                subject = claims.Subject;
            }

            return new AntigravityCliCredential(
                AccessToken: accessToken,
                RefreshToken: refreshToken,
                IdToken: idToken,
                Email: email,
                Subject: subject,
                SourceLabel: sourceLabel);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string? Email, string? Subject) ParseJwtClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);

            var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadBase64.Length % 4)
            {
                case 2: payloadBase64 += "=="; break;
                case 3: payloadBase64 += "="; break;
            }

            var bytes = Convert.FromBase64String(payloadBase64);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            string? email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
            string? sub = root.TryGetProperty("sub", out var s) ? s.GetString() : null;
            return (email, sub);
        }
        catch
        {
            return (null, null);
        }
    }
}

public sealed class AntigravityCliSource : ISignalSource
{
    private const string UserAgent = "antigravity/hub/2.9.1 windows/amd64";
    private const string LoadCodeAssistUrl = "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private const string RetrieveUserQuotaSummaryUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    private const string RetrieveUserQuotaUrl = "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota";

    private readonly AppSettings? _settings;
    private readonly IAntigravityCliCredentialReader _credentialReader;
    private readonly HttpClient _httpClient;

    public ProviderProfile Profile => ProviderCatalog.AntigravityCli;

    public AntigravityCliSource(
        AppSettings? settings = null,
        IAntigravityCliCredentialReader? credentialReader = null,
        HttpClient? httpClient = null)
    {
        _settings = settings;
        _credentialReader = credentialReader ?? new WindowsAntigravityCliCredentialReader();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        var creds = _credentialReader.ReadCredential(_settings);
        if (creds is null)
        {
            var isExplicitAuthPath = !string.IsNullOrWhiteSpace(_settings?.AntigravityCliAuthPath);
            var missingSummary = isExplicitAuthPath
                ? "Configured Antigravity CLI auth file not found"
                : "Antigravity CLI credential not found (sign in via Zeron Antigravity integration)";

            return new ProviderReading(
                Usage: null,
                Identity: null,
                StatusSummary: missingSummary,
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Cli);
        }

        // Step 1: POST loadCodeAssist
        using var loadRequest = new HttpRequestMessage(HttpMethod.Post, LoadCodeAssistUrl);
        loadRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        loadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        loadRequest.Content = new StringContent(
            "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}",
            Encoding.UTF8,
            "application/json");

        string? projectId = null;
        string? planName = null;

        try
        {
            using var loadResponse = await _httpClient.SendAsync(loadRequest, cancellationToken).ConfigureAwait(false);
            if (loadResponse.StatusCode == HttpStatusCode.Unauthorized || loadResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                var unauthIdentity = new IdentityCard(
                    ProviderId: Profile.ProviderId,
                    DisplayName: Profile.DisplayName,
                    Email: creds.Email,
                    Org: null,
                    Plan: null,
                    LoginMethod: creds.SourceLabel);

                return new ProviderReading(
                    Usage: null,
                    Identity: unauthIdentity,
                    StatusSummary: "Antigravity CLI session expired (re-authenticate via Zeron Antigravity integration)",
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Cli);
            }

            if (!loadResponse.IsSuccessStatusCode)
            {
                return new ProviderReading(
                    Usage: null,
                    Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, creds.Email, null, null, creds.SourceLabel),
                    StatusSummary: $"Cloud Code API error ({(int)loadResponse.StatusCode})",
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Cli);
            }

            var loadJson = await loadResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var loadDoc = JsonDocument.Parse(loadJson);
            var root = loadDoc.RootElement;

            if (root.TryGetProperty("cloudaicompanionProject", out var cp) && cp.ValueKind == JsonValueKind.String)
            {
                projectId = cp.GetString();
            }

            // Prefer paidTier.name over currentTier.name
            if (root.TryGetProperty("paidTier", out var pt) && pt.TryGetProperty("name", out var ptn))
            {
                planName = ptn.GetString();
            }
            else if (root.TryGetProperty("currentTier", out var ct) && ct.TryGetProperty("name", out var ctn))
            {
                planName = ctn.GetString();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ProviderReading(
                Usage: null,
                Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, creds.Email, null, null, creds.SourceLabel),
                StatusSummary: "Connection to Cloud Code failed",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Cli);
        }

        var identity = new IdentityCard(
            ProviderId: Profile.ProviderId,
            DisplayName: Profile.DisplayName,
            Email: creds.Email,
            Org: null,
            Plan: !string.IsNullOrWhiteSpace(planName) ? planName : null,
            LoginMethod: creds.SourceLabel);

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return new ProviderReading(
                Usage: null,
                Identity: identity,
                StatusSummary: "No companion project associated",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Cli);
        }

        // Step 2: POST retrieveUserQuotaSummary
        AntigravityQuotaResult? quotaResult = null;
        var projectPayload = JsonSerializer.Serialize(new { project = projectId });
        bool sessionExpired = false;
        bool accessForbidden = false;

        try
        {
            using var summaryRequest = new HttpRequestMessage(HttpMethod.Post, RetrieveUserQuotaSummaryUrl);
            summaryRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            summaryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            summaryRequest.Content = new StringContent(projectPayload, Encoding.UTF8, "application/json");

            using var summaryResponse = await _httpClient.SendAsync(summaryRequest, cancellationToken).ConfigureAwait(false);
            if (summaryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                sessionExpired = true;
            }
            else if (summaryResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                accessForbidden = true;
            }
            else if (summaryResponse.IsSuccessStatusCode)
            {
                var summaryJson = await summaryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                quotaResult = AntigravityQuotaParser.ParseQuotaSummary(summaryJson);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall through to retrieveUserQuota if not auth error
        }

        // Fallback: POST retrieveUserQuota if needed and not unauthorized/forbidden
        if (!sessionExpired && !accessForbidden && (quotaResult is null || quotaResult.Buckets.Count == 0))
        {
            try
            {
                using var fallbackRequest = new HttpRequestMessage(HttpMethod.Post, RetrieveUserQuotaUrl);
                fallbackRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                fallbackRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
                fallbackRequest.Content = new StringContent(projectPayload, Encoding.UTF8, "application/json");

                using var fallbackResponse = await _httpClient.SendAsync(fallbackRequest, cancellationToken).ConfigureAwait(false);
                if (fallbackResponse.StatusCode == HttpStatusCode.Unauthorized)
                {
                    sessionExpired = true;
                }
                else if (fallbackResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    accessForbidden = true;
                }
                else if (fallbackResponse.IsSuccessStatusCode)
                {
                    var fallbackJson = await fallbackResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    quotaResult = AntigravityQuotaParser.ParseQuotaSummary(fallbackJson);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fallback failed
            }
        }

        if (sessionExpired)
        {
            return new ProviderReading(
                Usage: null,
                Identity: identity,
                StatusSummary: "Antigravity CLI session expired (re-authenticate via Zeron Antigravity integration)",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Cli);
        }

        if (accessForbidden)
        {
            return new ProviderReading(
                Usage: null,
                Identity: identity,
                StatusSummary: "Antigravity CLI quota access forbidden (403)",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Cli);
        }

        var buckets = quotaResult?.Buckets ?? Array.Empty<NamedQuotaBucket>();

        var usage = new UsagePulse(
            ProviderId: Profile.ProviderId,
            CapturedAt: now,
            SessionUsed: null,
            SessionLimit: null,
            WeekUsed: null,
            WeekLimit: null,
            SpendingBucket: null,
            Consumption: null,
            SessionWindow: null,
            WeekWindow: null,
            QuotaBuckets: buckets,
            AccountEmail: creds.Email,
            AccountId: creds.Subject ?? creds.Email);

        var statusSummary = buckets.Count > 0
            ? $"Live ({buckets.Count} quotas)"
            : "Live (limits not available)";

        return new ProviderReading(
            Usage: usage,
            Identity: identity,
            StatusSummary: statusSummary,
            CapturedAt: now,
            Confidence: ReadingConfidence.High,
            Source: ReadingSource.Api);
    }
}
