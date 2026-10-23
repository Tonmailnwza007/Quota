using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Costats.Bridge.Antigravity;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;

namespace Costats.Bridge.Tests;

public sealed class AntigravityBackgroundSourceTests
{
    private const string Email = "background-quota@example.test";
    private const string Subject = "synthetic-background-subject";
    private const string Access = "SYNTHETIC_BACKGROUND_ACCESS";
    private const string Refresh = "SYNTHETIC_BACKGROUND_REFRESH";
    private const string RenewedAccess = "SYNTHETIC_BACKGROUND_RENEWED";
    private const string PrivateResponse = "SYNTHETIC_PRIVATE_SERVER_RESPONSE";
    private const string GroupedSummary = "{\"groups\":[{\"displayName\":\"Models\",\"buckets\":[{\"bucketId\":\"model-pro\",\"displayName\":\"Pro\",\"remainingFraction\":0.65,\"resetTime\":\"2030-01-07T12:00:00Z\"}]}]}";
    private const string UnmeasuredSummary = "{\"groups\":[{\"displayName\":\"Models\",\"buckets\":[{\"bucketId\":\"model-pro\",\"displayName\":\"Pro\",\"remainingFraction\":null}]}]}";

    [Fact]
    public async Task Remote_PrimaryGroupedSummaryUsesVerifiedProjectContext()
    {
        var store = new MemoryStore(Credential(AntigravityCredentialLocation.Shared));
        string? summaryBody = null;
        using var handler = new FakeTransport(async (request, cancellation) =>
        {
            if (IsIdentity(request)) return IdentityResponse();
            Assert.Equal("https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary", request.RequestUri!.AbsoluteUri);
            summaryBody = await request.Content!.ReadAsStringAsync(cancellation);
            return Response(HttpStatusCode.OK, GroupedSummary);
        });
        using var client = Client(handler);
        var source = Remote(store, client);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal("synthetic-project", JsonDocument.Parse(summaryBody!).RootElement.GetProperty("project").GetString());
        Assert.Equal(1, handler.QuotaRequests);
        Assert.Equal(1, handler.LoadRequests);
        Assert.Equal(0, handler.RefreshRequests);
        Assert.Equal(ReadingConfidence.High, reading.Confidence);
        Assert.Equal(Email, reading.Identity!.Email);
        Assert.Equal(AntigravityRemoteSource.SavedLoginMethod, reading.Identity.LoginMethod);
        Assert.Equal("Saved sign-in", StateMapper.Map("antigravity", reading).SourceLabel);
        Assert.InRange(Assert.Single(reading.Usage!.QuotaBuckets!).UsedPercent!.Value, 34.9f, 35.1f);
        Assert.Equal("ok", StateMapper.Map("antigravity", reading).Status);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task Remote_Quota401RenewsExactlyOnce_ThenRetriesWithRenewedAccess()
    {
        var store = new MemoryStore(Credential(AntigravityCredentialLocation.Shared));
        var quotas = 0;
        var quotaAccess = new List<string?>();
        using var handler = new FakeTransport((request, _) =>
        {
            if (IsIdentity(request)) return Task.FromResult(IdentityResponse());
            if (IsRenewal(request)) return Task.FromResult(RenewalResponse());
            quotaAccess.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(Interlocked.Increment(ref quotas) == 1
                ? Response(HttpStatusCode.Unauthorized, PrivateResponse) : Response(HttpStatusCode.OK, GroupedSummary));
        });
        using var client = Client(handler);

        var reading = await Remote(store, client).ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingConfidence.High, reading.Confidence);
        Assert.Equal(2, handler.QuotaRequests);
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(2, handler.IdentityRequests);
        Assert.Equal(new[] { Access, RenewedAccess }, quotaAccess);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(RenewedAccess, store.Current(AntigravityCredentialLocation.Shared)!.Credentials.AccessToken);
    }

    [Fact]
    public async Task Remote_RepeatedQuota401StopsAfterOneRenewal_AndReportsSignInRequired()
    {
        var store = new MemoryStore(Credential(AntigravityCredentialLocation.Shared));
        using var handler = new FakeTransport((request, _) => Task.FromResult(IsIdentity(request) ? IdentityResponse()
            : IsRenewal(request) ? RenewalResponse() : Response(HttpStatusCode.Unauthorized, PrivateResponse)));
        using var client = Client(handler);

        var reading = await Remote(store, client).ReadAsync(CancellationToken.None);

        Assert.Null(reading.Usage);
        Assert.Equal("expired", StateMapper.Map("antigravity", reading).Status);
        Assert.Equal(2, handler.QuotaRequests);
        Assert.Equal(1, handler.RefreshRequests);
        Assert.Equal(1, store.SaveCalls);
        Assert.DoesNotContain(PrivateResponse, WireJson.Serialize(StateMapper.Map("antigravity", reading)));
    }

    [Fact]
    public async Task Remote_Quota403DoesNotRenewOrMislabelAsExpired()
    {
        var store = new MemoryStore(Credential(AntigravityCredentialLocation.Shared));
        using var handler = new FakeTransport((request, _) => Task.FromResult(IsIdentity(request)
            ? IdentityResponse() : Response(HttpStatusCode.Forbidden, PrivateResponse)));
        using var client = Client(handler);

        var reading = await Remote(store, client).ReadAsync(CancellationToken.None);
        var dto = StateMapper.Map("antigravity", reading);

        Assert.Null(reading.Usage);
        Assert.Equal("error", dto.Status);
        Assert.Equal(new AntigravitySessionException(AntigravityFailure.Forbidden).Message, dto.StatusMessage);
        Assert.DoesNotContain("expired", dto.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RefreshRequests);
        Assert.Equal(1, handler.QuotaRequests);
        Assert.Equal(0, store.SaveCalls);
        Assert.DoesNotContain(PrivateResponse, WireJson.Serialize(dto));
    }

    [Fact]
    public async Task Remote_AllUnmeasuredBucketsRemainLowConfidenceAndUnknown()
    {
        var store = new MemoryStore(Credential(AntigravityCredentialLocation.Shared));
        using var handler = new FakeTransport((request, _) => Task.FromResult(IsIdentity(request) ? IdentityResponse()
            : IsLoad(request) ? Response(HttpStatusCode.OK, "{\"cloudaicompanionProject\":\"synthetic-project\",\"paidTier\":{\"name\":\"Pro\"}}")
            : Response(HttpStatusCode.OK, UnmeasuredSummary)));
        using var client = Client(handler);

        var reading = await Remote(store, client).ReadAsync(CancellationToken.None);
        var dto = StateMapper.Map("antigravity", reading);

        Assert.Equal(ReadingConfidence.Low, reading.Confidence);
        Assert.Equal("unknown", dto.Status);
        Assert.Null(dto.SessionProgress);
        Assert.Null(dto.WeeklyProgress);
        Assert.NotEmpty(dto.NamedBuckets);
        Assert.All(dto.NamedBuckets, bucket => Assert.Null(bucket.Percentage));
        Assert.Equal(0, handler.RefreshRequests);
        Assert.Equal(1, handler.LoadRequests);
        Assert.Equal(0, store.SaveCalls);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task Remote_AllQuotaEndpointsUnavailable_MapOfflineWithoutRenewal(int code)
    {
        var store = new MemoryStore(Credential(AntigravityCredentialLocation.Shared));
        using var handler = new FakeTransport((request, _) => Task.FromResult(IsIdentity(request) ? IdentityResponse()
            : Response((HttpStatusCode)code, PrivateResponse)));
        using var client = Client(handler);

        var reading = await Remote(store, client).ReadAsync(CancellationToken.None);
        var dto = StateMapper.Map("antigravity", reading);

        Assert.Null(reading.Usage);
        Assert.Equal(ReadingConfidence.Low, reading.Confidence);
        Assert.Equal("offline", dto.Status);
        Assert.Equal(new AntigravitySessionException(AntigravityFailure.Connection).Message, dto.StatusMessage);
        Assert.Equal(0, handler.RefreshRequests);
        Assert.Equal(1, handler.LoadRequests);
        Assert.Equal(1, handler.QuotaRequests);
        Assert.DoesNotContain(PrivateResponse, WireJson.Serialize(dto));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task App_LocalMissingOrErrorFallsBackToRemote(bool localThrows)
    {
        var backgroundReading = Reading(50);
        var local = new StubSource(_ => localThrows ? throw new HttpRequestException(PrivateResponse) : Task.FromResult(Reading(null, email: null, includeUsage: false)));
        var background = new StubSource(_ => Task.FromResult(backgroundReading));
        var binding = new FakeBinding();
        var source = new AntigravityAppWithBackgroundSource(local, background, binding);

        var result = await source.ReadAsync(CancellationToken.None);

        Assert.Same(backgroundReading, result);
        Assert.Equal(1, local.Reads);
        Assert.Equal(1, background.Reads);
        Assert.Empty(binding.LocalEmails);
    }

    [Fact]
    public async Task App_MeasuredLocalZeroUsedWhenBackgroundUnavailableAndUpdatesAccountBinding()
    {
        var localReading = Reading(0);
        var local = new StubSource(_ => Task.FromResult(localReading));
        var background = new StubSource(_ => throw new InvalidOperationException("Background source must not run for measured local quota."));
        var binding = new FakeBinding();

        var result = await new AntigravityAppWithBackgroundSource(local, background, binding).ReadAsync(CancellationToken.None);

        Assert.Same(localReading, result);
        Assert.Equal(1, background.Reads);
        Assert.Equal(new[] { Email }, binding.LocalEmails);
        Assert.Equal(0f, Assert.Single(result.Usage!.QuotaBuckets!).UsedPercent);
    }

    [Fact]
    public async Task App_UnmeasuredLocalIdentityBindsBeforeRemoteFallback()
    {
        var local = new StubSource(_ => Task.FromResult(Reading(null)));
        var binding = new FakeBinding();
        var background = new StubSource(_ =>
        {
            Assert.Equal(new[] { Email }, binding.LocalEmails);
            return Task.FromResult(Reading(25));
        });

        var result = await new AntigravityAppWithBackgroundSource(local, background, binding).ReadAsync(CancellationToken.None);

        Assert.Equal(1, background.Reads);
        Assert.Equal(25f, Assert.Single(result.Usage!.QuotaBuckets!).UsedPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task App_UserCancellationPropagatesFromLocalOrBackground(bool cancelInBackground)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var local = new StubSource(token => cancelInBackground ? Task.FromResult(Reading(null, includeUsage: false)) : Task.FromCanceled<ProviderReading>(token));
        var background = new StubSource(token => Task.FromCanceled<ProviderReading>(token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AntigravityAppWithBackgroundSource(local, background, new FakeBinding()).ReadAsync(cancellation.Token));

        Assert.Equal(cancelInBackground ? 1 : 0, background.Reads);
    }

    [Fact]
    public async Task Remote_UserCancellationPropagatesWithoutCredentialWrites()
    {
        var store = new MemoryStore(Credential(AntigravityCredentialLocation.Shared));
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeTransport(async (request, cancellation) =>
        {
            if (IsIdentity(request)) return IdentityResponse();
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return Response(HttpStatusCode.OK, GroupedSummary);
        });
        using var client = Client(handler);
        using var cancellation = new CancellationTokenSource();
        var task = Remote(store, client).ReadAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public void AccountBinding_PersistsNormalizedHashAndRejectsDifferentRemoteAfterRestart()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "binding", "antigravity-account");
        var binding = new AntigravityAccountBinding(path);
        binding.SetFromLocal("  BACKGROUND-QUOTA@EXAMPLE.TEST  ");
        var persisted = File.ReadAllText(path).Trim();

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Email))), persisted);
        Assert.DoesNotContain(Email, persisted, StringComparison.OrdinalIgnoreCase);
        var restarted = new AntigravityAccountBinding(path);
        Assert.True(restarted.MatchOrBindRemote(Email));
        Assert.False(restarted.MatchOrBindRemote("someone-else@example.test"));
        Assert.Equal(persisted, File.ReadAllText(path).Trim());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void AccountBinding_InitialRemoteBindSurvivesRestart_TrustedLocalMayRebind()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "remote-binding");
        Assert.True(new AntigravityAccountBinding(path).MatchOrBindRemote(Email));
        var restarted = new AntigravityAccountBinding(path);
        Assert.False(restarted.MatchOrBindRemote("new-local@example.test"));
        restarted.SetFromLocal("new-local@example.test");
        var afterLocalChange = new AntigravityAccountBinding(path);
        Assert.True(afterLocalChange.MatchOrBindRemote("NEW-LOCAL@EXAMPLE.TEST"));
        Assert.False(afterLocalChange.MatchOrBindRemote(Email));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("seconds")]
    [InlineData("milliseconds")]
    [InlineData("string")]
    public void FileStore_PreservesUnknownFieldsAuthMethodRefreshAndExpirySchema(string schema)
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "synthetic-credential.json");
        var location = new AntigravityCredentialLocation(path, "");
        var oldExpiry = new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newExpiry = new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var token = new Dictionary<string, object?>
        {
            ["access_token"] = Access, ["refresh_token"] = Refresh, ["token_type"] = "Bearer",
            ["unknown_token_field"] = "must-remain"
        };
        token[schema == "expiry" ? "expiry" : "expires_at"] = schema switch
        {
            "seconds" => oldExpiry.ToUnixTimeSeconds(), "milliseconds" => oldExpiry.ToUnixTimeMilliseconds(),
            _ => oldExpiry.ToString("O")
        };
        var original = JsonSerializer.Serialize(new
        {
            auth_method = "synthetic-owner-auth", id_token = Jwt(), token,
            unknown_root = new { keep = new object[] { 1, "unchanged", true } }
        });
        File.WriteAllText(path, original, new UTF8Encoding(false));
        var store = new WindowsAntigravityCredentialStore();
        var expected = store.Read(location)!;
        var replacement = expected.WithTokens(RenewedAccess, null, null, newExpiry);

        Assert.True(store.TrySave(expected, replacement));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var saved = root.GetProperty("token");
        Assert.Equal("synthetic-owner-auth", root.GetProperty("auth_method").GetString());
        Assert.Equal("unchanged", root.GetProperty("unknown_root").GetProperty("keep")[1].GetString());
        Assert.Equal("must-remain", saved.GetProperty("unknown_token_field").GetString());
        Assert.Equal("Bearer", saved.GetProperty("token_type").GetString());
        Assert.Equal(Refresh, saved.GetProperty("refresh_token").GetString());
        Assert.Equal(RenewedAccess, saved.GetProperty("access_token").GetString());
        Assert.Equal(Jwt(), root.GetProperty("id_token").GetString());
        if (schema == "seconds") Assert.Equal(newExpiry.ToUnixTimeSeconds(), saved.GetProperty("expires_at").GetInt64());
        else if (schema == "milliseconds") Assert.Equal(newExpiry.ToUnixTimeMilliseconds(), saved.GetProperty("expires_at").GetInt64());
        else Assert.Equal(newExpiry, DateTimeOffset.Parse(saved.GetProperty(schema == "expiry" ? "expiry" : "expires_at").GetString()!));
        Assert.Equal(newExpiry, store.Read(location)!.Credentials.ExpiresAt);
    }

    [Fact]
    public void FileStore_ObservedExternalChangeIsNeverOverwritten()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "synthetic-credential.json");
        var location = new AntigravityCredentialLocation(path, "");
        File.WriteAllText(path, CredentialJson(Access));
        var store = new WindowsAntigravityCredentialStore();
        var expected = store.Read(location)!;
        var externalJson = CredentialJson("SYNTHETIC_EXTERNAL_OWNER_SIGNIN");
        File.WriteAllText(path, externalJson);

        var saved = store.TrySave(expected, expected.WithTokens(RenewedAccess, null, null, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.False(saved);
        Assert.Equal(externalJson, File.ReadAllText(path));
    }

    [Fact]
    public void CliLocation_ExplicitFileOverridesTargetAndMissingFileNeverFallsBackToShared()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "missing-synthetic-auth.json");
        var settings = new AppSettings { AntigravityCliAuthPath = path, AntigravityCliCredentialTarget = "synthetic:custom-target" };

        var selected = AntigravityCredentialLocation.ForCli(settings);

        Assert.Equal(Path.GetFullPath(path), selected.FilePath);
        Assert.NotEqual(AntigravityCredentialLocation.Shared.Key, selected.Key);
        Assert.Null(new WindowsAntigravityCredentialStore().Read(selected));
        settings.AntigravityCliAuthPath = null;
        var customTarget = AntigravityCredentialLocation.ForCli(settings);
        Assert.Equal("synthetic:custom-target", customTarget.TargetName);
        Assert.NotEqual(AntigravityCredentialLocation.Shared.Key, customTarget.Key);
        settings.AntigravityCliCredentialTarget = null;
        Assert.Equal(AntigravityCredentialLocation.Shared.Key, AntigravityCredentialLocation.ForCli(settings).Key);
    }

    [Fact]
    public async Task Remote_CliExplicitLocationUsesOnlyItsOwnCredentialIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new AppSettings { AntigravityCliAuthPath = Path.Combine(temporary.Path, "synthetic-cli.json") };
        var selected = AntigravityCredentialLocation.ForCli(settings);
        var store = new MemoryStore(Credential(selected));
        using var handler = new FakeTransport((request, _) => Task.FromResult(IsIdentity(request) ? IdentityResponse() : Response(HttpStatusCode.OK, GroupedSummary)));
        using var client = Client(handler);
        var source = new AntigravityRemoteSource(ProviderCatalog.AntigravityCli, new AntigravitySessionManager(store, client),
            () => AntigravityCredentialLocation.ForCli(settings), client);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal("antigravity-cli", reading.Usage!.ProviderId);
        Assert.Equal(Email, reading.Identity!.Email);
        Assert.All(store.ReadKeys, key => Assert.Equal(selected.Key, key));
        Assert.DoesNotContain(AntigravityCredentialLocation.Shared.Key, store.ReadKeys);
    }

    public static IEnumerable<object[]> FailureCases() => Enum.GetValues<AntigravityFailure>().Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(FailureCases))]
    public void Mapper_OnlyFixedSafeAntigravityFailureMessagesCrossBoundary(AntigravityFailure failure)
    {
        var safe = new AntigravitySessionException(failure).Message;
        var reading = new ProviderReading(null, null, safe, DateTimeOffset.UtcNow, ReadingConfidence.Low, ReadingSource.Api);

        var dto = StateMapper.Map("antigravity", reading);

        Assert.Equal(safe, dto.StatusMessage);
        Assert.Equal(failure == AntigravityFailure.SignInRequired ? "expired" : failure == AntigravityFailure.Connection ? "offline" : failure is AntigravityFailure.UnverifiedQuota or AntigravityFailure.SourceSelectionRequired ? "unknown" : "error", dto.Status);
    }

    [Theory]
    [InlineData("antigravity")]
    [InlineData("antigravity-cli")]
    public void Mapper_ArbitraryProviderDiagnosticsLoginAndBucketDetailsAreNotSerialized(string id)
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new UsagePulse(id, now, null, null, null, null, null, null, null, null,
            [new NamedQuotaBucket("model", "Pro", 10f, StatusSummary: PrivateResponse)], StatusMessage: PrivateResponse);
        var reading = new ProviderReading(usage, new(id, id, Email, null, "Pro", PrivateResponse),
            "Quota error: " + PrivateResponse + " " + Refresh, now, ReadingConfidence.Low, ReadingSource.Api);

        var serialized = WireJson.Serialize(StateMapper.Map(id, reading));

        Assert.DoesNotContain(PrivateResponse, serialized);
        Assert.DoesNotContain(Refresh, serialized);
        Assert.Contains(Email, serialized);
    }

    private static AntigravityRemoteSource Remote(MemoryStore store, HttpClient client) => new(ProviderCatalog.Antigravity,
        new AntigravitySessionManager(store, client), () => AntigravityCredentialLocation.Shared, client);
    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private static bool IsIdentity(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == "https://openidconnect.googleapis.com/v1/userinfo";
    private static bool IsRenewal(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == "https://oauth2.googleapis.com/token";
    private static bool IsLoad(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal);
    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage IdentityResponse() => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { sub = Subject, email = Email, email_verified = true }));
    private static HttpResponseMessage RenewalResponse() => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = RenewedAccess, expires_in = 3600 }));
    private static AntigravityCredentialSnapshot Credential(AntigravityCredentialLocation location) => AntigravityCredentialSnapshot.Parse(location, CredentialJson(Access));
    private static string CredentialJson(string access) => JsonSerializer.Serialize(new
    {
        token = new { access_token = access, refresh_token = Refresh, expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O") }, id_token = Jwt(), auth_method = "synthetic"
    });
    private static string Jwt()
    {
        static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Encode("{\"alg\":\"none\"}") + "." + Encode(JsonSerializer.Serialize(new { sub = Subject, email = Email, aud = AntigravitySessionManager.ConsumerClientId })) + ".synthetic";
    }
    private static ProviderReading Reading(float? percentage, string? email = Email, bool includeUsage = true)
    {
        var now = DateTimeOffset.UtcNow;
        var usage = includeUsage ? new UsagePulse("antigravity", now, null, null, null, null, null, null, null, null,
            [new NamedQuotaBucket("local-model", "Local model", percentage)]) : null;
        return new(usage, email is null ? null : new("antigravity", "Antigravity", email, null, "Pro", "Local app"),
            includeUsage ? "Local quota" : "Antigravity is not running", now, includeUsage ? ReadingConfidence.High : ReadingConfidence.Low, ReadingSource.Api);
    }

    private sealed class FakeTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int QuotaRequests { get; private set; }
        public int LoadRequests { get; private set; }
        public int RefreshRequests { get; private set; }
        public int IdentityRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (IsIdentity(request)) IdentityRequests++;
            else if (IsRenewal(request)) RefreshRequests++;
            else
            {
                Assert.Contains(request.RequestUri!.Host, new[] { "cloudcode-pa.googleapis.com", "daily-cloudcode-pa.googleapis.com" });
                Assert.Equal(HttpMethod.Post, request.Method);
                if (IsLoad(request))
                {
                    LoadRequests++;
                    return Task.FromResult(Response(HttpStatusCode.OK, "{\"cloudaicompanionProject\":\"synthetic-project\",\"paidTier\":{\"name\":\"Pro\"}}"));
                }
                else QuotaRequests++;
            }
            return send(request, cancellationToken);
        }
    }
    private sealed class MemoryStore(params AntigravityCredentialSnapshot[] records) : IAntigravityCredentialStore
    {
        private readonly Dictionary<string, AntigravityCredentialSnapshot> _records = records.ToDictionary(value => value.Location.Key);
        public ConcurrentQueue<string> ReadKeys { get; } = new();
        public int SaveCalls { get; private set; }
        public AntigravityCredentialSnapshot? Read(AntigravityCredentialLocation location)
        {
            ReadKeys.Enqueue(location.Key);
            return Current(location);
        }
        public AntigravityCredentialSnapshot? Current(AntigravityCredentialLocation location) => _records.GetValueOrDefault(location.Key);
        public bool TrySave(AntigravityCredentialSnapshot expected, AntigravityCredentialSnapshot replacement)
        {
            SaveCalls++;
            if (Current(expected.Location)?.Fingerprint != expected.Fingerprint || replacement.Location.Key != expected.Location.Key) return false;
            _records[expected.Location.Key] = replacement;
            return true;
        }
    }
    private sealed class StubSource(Func<CancellationToken, Task<ProviderReading>> read) : ISignalSource
    {
        public ProviderProfile Profile => ProviderCatalog.Antigravity;
        public int Reads { get; private set; }
        public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken) { Reads++; return read(cancellationToken); }
    }
    private sealed class FakeBinding : IAntigravityAccountBinding
    {
        public List<string> LocalEmails { get; } = [];
        public void SetFromLocal(string email) => LocalEmails.Add(email);
        public bool MatchOrBindRemote(string email) => true;
    }
}
