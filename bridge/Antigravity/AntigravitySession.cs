using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Costats.Bridge.Antigravity;

public enum AntigravityFailure { SignInRequired, ClientMismatch, IdentityMismatch, Forbidden, Connection, InvalidResponse, Storage, CredentialChanged, UnverifiedQuota, SourceSelectionRequired }
public sealed class AntigravitySessionException(AntigravityFailure failure) : Exception(MessageFor(failure))
{
    public AntigravityFailure Failure { get; } = failure;
    private static string MessageFor(AntigravityFailure value) => value switch
    {
        AntigravityFailure.SignInRequired => "Antigravity session expired; sign in again in Antigravity or its CLI, then refresh.",
        AntigravityFailure.ClientMismatch => "Antigravity OAuth client error; sign in again using the current Antigravity client.",
        AntigravityFailure.IdentityMismatch => "Antigravity account mismatch; confirm the intended account in Antigravity before refreshing.",
        AntigravityFailure.Forbidden => "Antigravity quota access forbidden (403).",
        AntigravityFailure.Connection => "Antigravity quota connection unavailable; try again later.",
        AntigravityFailure.Storage => "Antigravity session state could not be saved; check credential store and settings folder access.",
        AntigravityFailure.CredentialChanged => "Antigravity sign-in changed during refresh; refresh again.",
        AntigravityFailure.UnverifiedQuota => "Antigravity did not return verified quota for this sign-in. Try again later or sign in again if this persists.",
        AntigravityFailure.SourceSelectionRequired => "Choose the Antigravity account source in Settings > Advanced settings, then save source settings.",
        _ => "Antigravity quota response error; try again later."
    };
}

public sealed record AntigravitySession(AntigravityCredentials Credentials, bool WasRefreshed,
    [property: System.Text.Json.Serialization.JsonIgnore] string? Fingerprint = null)
{
    public override string ToString() => $"AntigravitySession {{ WasRefreshed={WasRefreshed}, Credentials=<redacted> }}";
}

public sealed class AntigravitySessionManager
{
    // Public installed-application OAuth configuration, verified against agy 1.4.2.
    // Source/provenance: crossusage commit 5da900e77293e1b913f13a2a69414906d7223a29,
    // plugins/antigravity-cli/plugin.js. This is not a user's access/refresh token.
    public const string ConsumerClientId = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    private const string ConsumerClientSecret = "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";
    private readonly IAntigravityCredentialStore _store;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Fingerprint, AntigravityCredentials Credentials, DateTimeOffset Verified)> _verified = new();
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    public AntigravitySessionManager(IAntigravityCredentialStore store, HttpClient? httpClient = null)
        => (_store, _http) = (store, httpClient ?? CreateHttpClient());

    public async Task<AntigravitySession> AcquireAsync(AntigravityCredentialLocation location, CancellationToken cancellationToken, string? rejectedAccessToken = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _store.Read(location) ?? throw new AntigravitySessionException(AntigravityFailure.SignInRequired);
            var now = DateTimeOffset.UtcNow;
            var current = snapshot.Credentials;
            var needsRefresh = string.IsNullOrEmpty(current.AccessToken) || current.ExpiresAt <= now.AddSeconds(60) ||
                (rejectedAccessToken is not null && rejectedAccessToken == current.AccessToken);
            if (!needsRefresh && _verified.TryGetValue(location.Key, out var cached) && cached.Fingerprint == snapshot.Fingerprint &&
                now - cached.Verified < TimeSpan.FromMinutes(5)) return new(cached.Credentials, false, snapshot.Fingerprint);
            var refreshed = false;
            if (needsRefresh) { snapshot = await RefreshAsync(snapshot, cancellationToken).ConfigureAwait(false); refreshed = true; }
            AntigravityCredentials verified;
            try { verified = await VerifyIdentityAsync(snapshot.Credentials, current, cancellationToken).ConfigureAwait(false); }
            catch (AntigravitySessionException e) when (e.Failure == AntigravityFailure.SignInRequired && !refreshed)
            {
                snapshot = await RefreshAsync(snapshot, cancellationToken).ConfigureAwait(false);
                refreshed = true;
                verified = await VerifyIdentityAsync(snapshot.Credentials, current, cancellationToken).ConfigureAwait(false);
            }
            if (refreshed)
            {
                var beforeSave = _store.Read(location);
                if (beforeSave is null || beforeSave.Fingerprint != _refreshExpected!.Fingerprint)
                    throw new AntigravitySessionException(AntigravityFailure.CredentialChanged);
                if (!_store.TrySave(_refreshExpected, snapshot)) throw new AntigravitySessionException(AntigravityFailure.Storage);
            }
            _verified[location.Key] = (snapshot.Fingerprint, verified, now);
            return new(verified, refreshed, snapshot.Fingerprint);
        }
        catch (OperationCanceledException) { throw; }
        catch (AntigravitySessionException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AntigravitySessionException(AntigravityFailure.Storage); }
        catch (HttpRequestException) { throw new AntigravitySessionException(AntigravityFailure.Connection); }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { throw new AntigravitySessionException(AntigravityFailure.InvalidResponse); }
        finally { _refreshExpected = null; _gate.Release(); }
    }

    public bool IsCurrent(AntigravityCredentialLocation location, AntigravitySession session) =>
        session.Fingerprint is not null && _store.Read(location)?.Fingerprint == session.Fingerprint;

    // Access is serialized by _gate, so a refresh/save pair always uses the same snapshot.
    private AntigravityCredentialSnapshot? _refreshExpected;
    private async Task<AntigravityCredentialSnapshot> RefreshAsync(AntigravityCredentialSnapshot snapshot, CancellationToken cancellationToken)
    {
        var creds = snapshot.Credentials;
        if (string.IsNullOrWhiteSpace(creds.RefreshToken)) throw new AntigravitySessionException(AntigravityFailure.SignInRequired);
        var clientId = creds.ClientId;
        var clientSecret = creds.ClientSecret;
        if (string.IsNullOrEmpty(clientSecret))
        {
            if (clientId != ConsumerClientId) throw new AntigravitySessionException(AntigravityFailure.ClientMismatch);
            clientSecret = ConsumerClientSecret;
        }
        if (string.IsNullOrWhiteSpace(clientId)) throw new AntigravitySessionException(AntigravityFailure.ClientMismatch);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["client_secret"] = clientSecret,
                ["refresh_token"] = creds.RefreshToken
            })
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = "";
            try { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)); code = Text(doc.RootElement, "error"); }
            catch (JsonException) { }
            throw new AntigravitySessionException(code switch
            {
                "invalid_grant" => AntigravityFailure.SignInRequired,
                "invalid_client" or "unauthorized_client" => AntigravityFailure.ClientMismatch,
                _ when response.StatusCode == HttpStatusCode.Forbidden => AntigravityFailure.Forbidden,
                _ when response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500 => AntigravityFailure.Connection,
                _ => AntigravityFailure.SignInRequired
            });
        }
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var access = Text(payload.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(access) || !payload.RootElement.TryGetProperty("expires_in", out var duration) ||
            duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out var seconds) || seconds is <= 0 or > 86400)
            throw new AntigravitySessionException(AntigravityFailure.InvalidResponse);
        _refreshExpected = snapshot;
        return snapshot.WithTokens(access, Text(payload.RootElement, "refresh_token"), Text(payload.RootElement, "id_token"), DateTimeOffset.UtcNow.AddSeconds(seconds));
    }

    private async Task<AntigravityCredentials> VerifyIdentityAsync(AntigravityCredentials refreshed, AntigravityCredentials expected, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new AntigravitySessionException(AntigravityFailure.SignInRequired);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new AntigravitySessionException(AntigravityFailure.Forbidden);
        if (!response.IsSuccessStatusCode) throw new AntigravitySessionException(AntigravityFailure.Connection);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var subject = Text(doc.RootElement, "sub"); var email = Text(doc.RootElement, "email");
        var emailVerified = doc.RootElement.TryGetProperty("email_verified", out var verified) && verified.ValueKind == JsonValueKind.True;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email) || !emailVerified ||
            (!string.IsNullOrEmpty(expected.Subject) && subject != expected.Subject) ||
            (!string.IsNullOrEmpty(expected.Email) && !email.Equals(expected.Email, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(refreshed.Subject) && subject != refreshed.Subject) ||
            (!string.IsNullOrEmpty(refreshed.Email) && !email.Equals(refreshed.Email, StringComparison.OrdinalIgnoreCase)))
            throw new AntigravitySessionException(AntigravityFailure.IdentityMismatch);
        return refreshed with { Email = email, Subject = subject };
    }
    internal static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
