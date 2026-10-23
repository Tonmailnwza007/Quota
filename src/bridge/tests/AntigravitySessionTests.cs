using System.Net;
using System.Text;
using System.Text.Json;
using Costats.Bridge.Antigravity;

namespace Costats.Bridge.Tests;

public sealed class AntigravitySessionTests
{
    private const string Email = "quota-session@example.test";
    private const string Subject = "synthetic-google-subject";
    private const string OriginalAccess = "SYNTHETIC_ACCESS_ORIGINAL";
    private const string RenewedAccess = "SYNTHETIC_ACCESS_RENEWED";
    private const string OriginalRefresh = "SYNTHETIC_REFRESH_ORIGINAL";
    private static readonly AntigravityCredentialLocation Location = new(null, "synthetic:test-only");

    [Fact]
    public async Task ValidCredential_VerifiesIdentityWithoutRenewingOrWriting()
    {
        var original = Credential(expired: false);
        var store = new MemoryStore(original);
        using var handler = StandardHandler();
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var session = await manager.AcquireAsync(Location, CancellationToken.None);
        var again = await manager.AcquireAsync(Location, CancellationToken.None);

        Assert.False(session.WasRefreshed);
        Assert.False(again.WasRefreshed);
        Assert.Equal(OriginalAccess, session.Credentials.AccessToken);
        Assert.Equal(Email, session.Credentials.Email);
        Assert.Equal(Subject, session.Credentials.Subject);
        Assert.Equal(0, handler.RefreshRequests);
        Assert.Equal(1, handler.IdentityRequests);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Same(original, store.Current);
    }

    [Fact]
    public async Task DeletedCredential_InvalidatesVerifiedSessionWithoutRenewingOrRestoringIt()
    {
        var store = new MemoryStore(Credential(expired: false));
        using var handler = StandardHandler();
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);
        await manager.AcquireAsync(Location, CancellationToken.None);

        store.ReplaceExternally(null);
        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.SignInRequired, error.Failure);
        Assert.Equal(1, handler.IdentityRequests);
        Assert.Equal(0, handler.RefreshRequests);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task ExpiredCredential_RenewsAndPersists_PreservingOmittedRefreshToken()
    {
        var original = Credential(expired: true);
        var store = new MemoryStore(original);
        using var handler = StandardHandler();
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var session = await manager.AcquireAsync(Location, CancellationToken.None);

        Assert.True(session.WasRefreshed);
        Assert.Equal(RenewedAccess, session.Credentials.AccessToken);
        Assert.Equal(OriginalRefresh, session.Credentials.RefreshToken);
        Assert.Equal(original.Credentials.IdToken, session.Credentials.IdToken);
        Assert.True(session.Credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(1, handler.IdentityRequests);
        Assert.Equal(1, store.SaveAttempts);
        Assert.Equal(1, store.SuccessfulWrites);
        Assert.NotEqual(original.Fingerprint, store.Current!.Fingerprint);
        Assert.Equal(RenewedAccess, store.Current.Credentials.AccessToken);
        Assert.Equal(OriginalRefresh, store.Current.Credentials.RefreshToken);
    }

    [Fact]
    public async Task UserInfo401_RenewsOnceThenReverifiesAndPersists()
    {
        var store = new MemoryStore(Credential(expired: false));
        var identities = 0;
        using var handler = new RoutedHandler((request, _) => Task.FromResult(IsRefresh(request) ? RefreshResponse()
            : Interlocked.Increment(ref identities) == 1 ? Response(HttpStatusCode.Unauthorized, "{}") : IdentityResponse()));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var session = await manager.AcquireAsync(Location, CancellationToken.None);

        Assert.True(session.WasRefreshed);
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(2, handler.IdentityRequests);
        Assert.Equal(1, store.SuccessfulWrites);
    }

    [Fact]
    public async Task UserInfoStill401AfterRenewal_DoesNotLoopOrPersist()
    {
        var store = new MemoryStore(Credential(expired: false));
        using var handler = new RoutedHandler((request, _) => Task.FromResult(IsRefresh(request) ? RefreshResponse() : Response(HttpStatusCode.Unauthorized, "{}")));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.SignInRequired, error.Failure);
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(2, handler.IdentityRequests);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public async Task InvalidGrant_IsActionableSignInFailure_AndNeverWrites()
    {
        var original = Credential(expired: true);
        var store = new MemoryStore(original);
        using var handler = new RoutedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"SYNTHETIC_PRIVATE_RESPONSE\"}")));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.SignInRequired, error.Failure);
        Assert.Contains("sign in", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_RESPONSE", error.Message);
        Assert.DoesNotContain(OriginalRefresh, error.ToString());
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(0, handler.IdentityRequests);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Same(original, store.Current);
    }

    [Fact]
    public async Task UserInfo403_IsForbiddenRatherThanExpired_AndDoesNotRenew()
    {
        var original = Credential(expired: false);
        var store = new MemoryStore(original);
        using var handler = new RoutedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden, "{}")));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.Forbidden, error.Failure);
        Assert.DoesNotContain("expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RefreshRequests);
        Assert.Equal(1, handler.IdentityRequests);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Same(original, store.Current);
    }

    [Theory]
    [InlineData("another-subject", Email, true)]
    [InlineData(Subject, "another-person@example.test", true)]
    [InlineData(Subject, Email, false)]
    [InlineData("", Email, true)]
    [InlineData(Subject, "", true)]
    public async Task RefreshedIdentityMismatchOrUnverifiedEmail_IsRejectedBeforeWrite(string subject, string email, bool verified)
    {
        var original = Credential(expired: true);
        var store = new MemoryStore(original);
        using var handler = new RoutedHandler((request, _) => Task.FromResult(IsRefresh(request) ? RefreshResponse() : IdentityResponse(subject, email, verified)));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.IdentityMismatch, error.Failure);
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(1, handler.IdentityRequests);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Same(original, store.Current);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sub\":\"synthetic-google-subject\",\"email\":\"quota-session@example.test\"}")]
    [InlineData("{\"sub\":\"synthetic-google-subject\",\"email\":\"quota-session@example.test\",\"email_verified\":\"true\"}")]
    public async Task MissingOrNonBooleanEmailVerification_IsRejectedBeforeWrite(string identity)
    {
        var store = new MemoryStore(Credential(expired: true));
        using var handler = new RoutedHandler((request, _) => Task.FromResult(IsRefresh(request) ? RefreshResponse() : Response(HttpStatusCode.OK, identity)));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.IdentityMismatch, error.Failure);
        Assert.Equal(0, store.SaveAttempts);
    }

    [Fact]
    public async Task RefreshedIdTokenWithDifferentIdentity_IsRejectedBeforeWrite()
    {
        var original = Credential(expired: true);
        var store = new MemoryStore(original);
        var response = JsonSerializer.Serialize(new { access_token = RenewedAccess, expires_in = 3600, id_token = Jwt("different-subject", Email) });
        using var handler = new RoutedHandler((request, _) => Task.FromResult(IsRefresh(request) ? Response(HttpStatusCode.OK, response) : IdentityResponse()));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.IdentityMismatch, error.Failure);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Same(original, store.Current);
    }

    [Fact]
    public async Task TwoConcurrentAcquires_ShareOneRenewalAndOnePersist()
    {
        var store = new MemoryStore(Credential(expired: true));
        var entered = NewSignal();
        var release = NewSignal();
        using var handler = new RoutedHandler(async (request, cancellation) =>
        {
            if (!IsRefresh(request)) return IdentityResponse();
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellation);
            return RefreshResponse();
        });
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var first = manager.AcquireAsync(Location, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = manager.AcquireAsync(Location, CancellationToken.None);
        release.TrySetResult(true);
        var sessions = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(1, handler.IdentityRequests);
        Assert.Equal(1, store.SuccessfulWrites);
        Assert.Single(sessions, s => s.WasRefreshed);
        Assert.All(sessions, s => Assert.Equal(RenewedAccess, s.Credentials.AccessToken));
    }

    [Fact]
    public async Task CredentialReplacementDuringRefresh_IsObservedAndNeverOverwritten()
    {
        var store = new MemoryStore(Credential(expired: true));
        var replacement = Credential(expired: false, access: "SYNTHETIC_NEW_OWNER_SIGN_IN", subject: "new-owner-subject", email: "new-owner@example.test");
        using var handler = new RoutedHandler((request, _) =>
        {
            if (IsRefresh(request))
            {
                store.ReplaceExternally(replacement);
                return Task.FromResult(RefreshResponse());
            }
            return Task.FromResult(IdentityResponse());
        });
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.CredentialChanged, error.Failure);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Same(replacement, store.Current);
    }

    [Fact]
    public async Task CancellationDuringRenewal_PropagatesAndReleasesGateForNextAcquire()
    {
        var store = new MemoryStore(Credential(expired: true));
        var entered = NewSignal();
        var attempts = 0;
        using var handler = new RoutedHandler(async (request, cancellation) =>
        {
            if (!IsRefresh(request)) return IdentityResponse();
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            return RefreshResponse();
        });
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);
        using var cancellation = new CancellationTokenSource();

        var first = manager.AcquireAsync(Location, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(0, store.SaveAttempts);

        var recovered = await manager.AcquireAsync(Location, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(recovered.WasRefreshed);
        Assert.Equal(2, handler.RefreshRequests);
        Assert.Equal(1, store.SuccessfulWrites);
    }

    [Fact]
    public async Task CancellationWhileWaitingForGate_DoesNotReleaseAnotherAcquireGate()
    {
        var store = new MemoryStore(Credential(expired: true));
        var entered = NewSignal();
        var release = NewSignal();
        using var handler = new RoutedHandler(async (request, cancellation) =>
        {
            if (!IsRefresh(request)) return IdentityResponse();
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellation);
            return RefreshResponse();
        });
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);
        var first = manager.AcquireAsync(Location, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var waiting = manager.AcquireAsync(Location, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, handler.RefreshRequests);
        release.TrySetResult(true);
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False((await manager.AcquireAsync(Location, CancellationToken.None)).WasRefreshed);
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(1, store.SuccessfulWrites);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"access_token\":\"fresh\"}")]
    [InlineData("{\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\" \",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":123,\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"fresh\",\"expires_in\":\"3600\"}")]
    [InlineData("{\"access_token\":\"fresh\",\"expires_in\":0}")]
    [InlineData("{\"access_token\":\"fresh\",\"expires_in\":-1}")]
    [InlineData("{\"access_token\":\"fresh\",\"expires_in\":86401}")]
    public async Task MalformedRefreshResponse_IsRejectedWithoutPersistence(string response)
    {
        var original = Credential(expired: true);
        var store = new MemoryStore(original);
        using var handler = new RoutedHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, response)));
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        var error = await Assert.ThrowsAsync<AntigravitySessionException>(() => manager.AcquireAsync(Location, CancellationToken.None));

        Assert.Equal(AntigravityFailure.InvalidResponse, error.Failure);
        Assert.Equal(0, handler.IdentityRequests);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Same(original, store.Current);
    }

    [Fact]
    public async Task RefreshTokenFormEncoding_RoundTripsPlusSlashAmpersandAndPercent()
    {
        const string specialRefresh = "synthetic+refresh/with&percent%value=tail?";
        var store = new MemoryStore(Credential(expired: true, refresh: specialRefresh));
        string? encoded = null;
        string? contentType = null;
        using var handler = new RoutedHandler(async (request, cancellation) =>
        {
            if (!IsRefresh(request)) return IdentityResponse();
            encoded = await request.Content!.ReadAsStringAsync(cancellation);
            contentType = request.Content.Headers.ContentType?.MediaType;
            return RefreshResponse();
        });
        using var client = Client(handler);
        var manager = new AntigravitySessionManager(store, client);

        await manager.AcquireAsync(Location, CancellationToken.None);

        Assert.Equal("application/x-www-form-urlencoded", contentType);
        var fields = encoded!.Split('&').Select(part => part.Split('=', 2)).ToDictionary(part =>
            WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part.Length > 1 ? part[1] : ""));
        Assert.Equal(specialRefresh, fields["refresh_token"]);
        Assert.Equal("refresh_token", fields["grant_type"]);
        Assert.Equal(AntigravitySessionManager.ConsumerClientId, fields["client_id"]);
        Assert.Contains("%2B", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%2F", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%26", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%25", encoded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(specialRefresh, store.Current!.Credentials.RefreshToken);
    }

    [Fact]
    public void CredentialSnapshotSession_ToStringAndSerializationDoNotDiscloseTokens()
    {
        const string clientSecret = "SYNTHETIC_CLIENT_SECRET";
        var snapshot = Credential(expired: true, clientSecret: clientSecret);
        var session = new AntigravitySession(snapshot.Credentials, true);
        var representations = new[]
        {
            snapshot.ToString(), snapshot.Credentials.ToString(), session.ToString(),
            JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(snapshot.Credentials), JsonSerializer.Serialize(session)
        };
        foreach (var representation in representations)
        {
            Assert.DoesNotContain(OriginalAccess, representation);
            Assert.DoesNotContain(OriginalRefresh, representation);
            Assert.DoesNotContain(snapshot.Credentials.IdToken!, representation);
            Assert.DoesNotContain(clientSecret, representation);
        }
    }

    private static AntigravityCredentialSnapshot Credential(bool expired, string access = OriginalAccess, string refresh = OriginalRefresh,
        string subject = Subject, string email = Email, string? clientSecret = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            token = new { access_token = access, refresh_token = refresh, expiry = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 2).ToString("O") },
            id_token = Jwt(subject, email), client_secret = clientSecret
        });
        return AntigravityCredentialSnapshot.Parse(Location, json);
    }

    private static string Jwt(string subject, string email)
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." +
            Encode(JsonSerializer.Serialize(new { sub = subject, email, aud = AntigravitySessionManager.ConsumerClientId })) + ".synthetic-signature";
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool IsRefresh(HttpRequestMessage request) => request.RequestUri?.AbsoluteUri == "https://oauth2.googleapis.com/token";
    private static HttpResponseMessage RefreshResponse() => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = RenewedAccess, expires_in = 3600 }));
    private static HttpResponseMessage IdentityResponse(string subject = Subject, string email = Email, bool verified = true)
        => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { sub = subject, email, email_verified = verified }));
    private static HttpResponseMessage Response(HttpStatusCode code, string content) => new(code) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    private static RoutedHandler StandardHandler() => new((request, _) => Task.FromResult(IsRefresh(request) ? RefreshResponse() : IdentityResponse()));

    private sealed class RoutedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _refreshRequests;
        private int _identityRequests;
        public int RefreshRequests => Volatile.Read(ref _refreshRequests);
        public int IdentityRequests => Volatile.Read(ref _identityRequests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (IsRefresh(request))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Interlocked.Increment(ref _refreshRequests);
            }
            else
            {
                Assert.Equal("https://openidconnect.googleapis.com/v1/userinfo", request.RequestUri?.AbsoluteUri);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Interlocked.Increment(ref _identityRequests);
            }
            return send(request, cancellationToken);
        }
    }

    private sealed class MemoryStore(AntigravityCredentialSnapshot initial) : IAntigravityCredentialStore
    {
        private readonly object _gate = new();
        private AntigravityCredentialSnapshot? _current = initial;
        private int _saveAttempts;
        private int _successfulWrites;
        public int SaveAttempts => Volatile.Read(ref _saveAttempts);
        public int SuccessfulWrites => Volatile.Read(ref _successfulWrites);
        public AntigravityCredentialSnapshot? Current { get { lock (_gate) return _current; } }
        public AntigravityCredentialSnapshot? Read(AntigravityCredentialLocation location)
        {
            lock (_gate) return _current?.Location.Key == location.Key ? _current : null;
        }
        public void ReplaceExternally(AntigravityCredentialSnapshot? replacement) { lock (_gate) _current = replacement; }
        public bool TrySave(AntigravityCredentialSnapshot expected, AntigravityCredentialSnapshot replacement)
        {
            Interlocked.Increment(ref _saveAttempts);
            lock (_gate)
            {
                if (_current is null || _current.Fingerprint != expected.Fingerprint || replacement.Location.Key != expected.Location.Key) return false;
                _current = replacement;
                Interlocked.Increment(ref _successfulWrites);
                return true;
            }
        }
    }
}
