using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Costats.Bridge.Antigravity;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;

namespace Costats.Bridge.Tests;

public sealed class AntigravityAuthorityTests
{
    private const string Email = "authority-check@example.test";
    private const string Subject = "synthetic-google-sub-authority";
    private const string Project = "synthetic-project";
    private const string ExpectedAgent = "antigravity/cli/1.4.2 windows/amd64";
    private const string AuthoritativeHost = "daily-cloudcode-pa.googleapis.com";
    private const string PrivateResponse = "SYNTHETIC_PRIVATE_CONTEXT_RESPONSE";

    [Theory]
    [InlineData("string")]
    [InlineData("id")]
    [InlineData("projectId")]
    public async Task Remote_UsesExactAuthoritativeHostAndCliAgent_LoadsContextBeforeScopedSummary(string projectShape)
    {
        using var transport = new Transport((request, _) => Task.FromResult(IsIdentity(request) ? Identity()
            : IsContext(request) ? Json(HttpStatusCode.OK, Context(projectShape)) : Json(HttpStatusCode.OK, Summary(0.4732))));
        using var client = Client(transport);

        var reading = await Remote(client).ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingConfidence.High, reading.Confidence);
        Assert.NotNull(reading.Usage);
        Assert.InRange(Assert.Single(reading.Usage.QuotaBuckets!).UsedPercent!.Value, 52.67f, 52.69f);
        var requests = transport.CloudRequests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.EndsWith(":loadCodeAssist", requests[0].Uri.AbsolutePath);
        Assert.EndsWith(":retrieveUserQuotaSummary", requests[1].Uri.AbsolutePath);
        Assert.All(requests, request =>
        {
            Assert.Equal(AuthoritativeHost, request.Uri.Host);
            Assert.Equal(ExpectedAgent, request.Agent);
            Assert.Equal(HttpMethod.Post, request.Method);
        });
        using var scope = JsonDocument.Parse(requests[1].Body);
        Assert.Equal(Project, scope.RootElement.GetProperty("project").GetString());
        Assert.Single(scope.RootElement.EnumerateObject());
        using var contextRequest = JsonDocument.Parse(requests[0].Body);
        Assert.Equal("ANTIGRAVITY", contextRequest.RootElement.GetProperty("metadata").GetProperty("ideType").GetString());
        Assert.DoesNotContain(requests, request => request.Uri.AbsolutePath.EndsWith(":onboardUser", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.Uri.Host == "cloudcode-pa.googleapis.com");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ineligibleTiers\":[{\"reasonCode\":\"GOOGLE_TOS_NOT_SUPPORTED_BY_CLIENT\"}]}")]
    [InlineData("[]")]
    [InlineData("not JSON")]
    public async Task Remote_UnsupportedOrInvalidContextCannotPromoteNumericPlaceholderQuota(string context)
    {
        using var transport = new Transport((request, _) => Task.FromResult(IsIdentity(request) ? Identity()
            : IsContext(request) ? Json(HttpStatusCode.OK, context) : Json(HttpStatusCode.OK, Summary(1))));
        using var client = Client(transport);

        var reading = await Remote(client).ReadAsync(CancellationToken.None);

        Assert.NotEqual(ReadingConfidence.High, reading.Confidence);
        Assert.DoesNotContain(transport.CloudRequests, request => !request.Uri.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal));
        Assert.Equal(0, transport.Renewals);
        var mapped = StateMapper.Map("antigravity-cli", reading);
        Assert.NotEqual("ok", mapped.Status);
        Assert.All(mapped.NamedBuckets, bucket => Assert.Null(bucket.Percentage));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(503)]
    public async Task Remote_ContextFailureStopsBeforeAnyQuotaRequest(int status)
    {
        using var transport = new Transport((request, _) => Task.FromResult(IsIdentity(request) ? Identity()
            : IsContext(request) ? Json((HttpStatusCode)status, PrivateResponse) : Json(HttpStatusCode.OK, Summary(1))));
        using var client = Client(transport);

        var reading = await Remote(client).ReadAsync(CancellationToken.None);

        Assert.NotEqual(ReadingConfidence.High, reading.Confidence);
        Assert.Single(transport.CloudRequests);
        Assert.All(transport.CloudRequests, request => Assert.EndsWith(":loadCodeAssist", request.Uri.AbsolutePath));
        Assert.Equal(0, transport.Renewals);
        Assert.DoesNotContain(PrivateResponse, WireJson.Serialize(StateMapper.Map("antigravity-cli", reading)));
    }

    [Fact]
    public async Task Remote_LegitimateFullQuotaRemainsKnownAfterValidContext()
    {
        using var transport = new Transport((request, _) => Task.FromResult(IsIdentity(request) ? Identity()
            : IsContext(request) ? Json(HttpStatusCode.OK, Context("string")) : Json(HttpStatusCode.OK, Summary(1))));
        using var client = Client(transport);

        var reading = await Remote(client).ReadAsync(CancellationToken.None);
        var mapped = StateMapper.Map("antigravity-cli", reading);

        Assert.Equal(ReadingConfidence.High, reading.Confidence);
        Assert.Equal("ok", mapped.Status);
        Assert.Equal(0d, Assert.Single(mapped.NamedBuckets).Percentage); //Known 100% remaining, not unknown.
        Assert.NotNull(mapped.NamedBuckets[0].ResetAt);
    }

    [Fact]
    public async Task Remote_AuthoritativeSummaryUnavailableDoesNotFallBackToKnownPlaceholderHost()
    {
        using var transport = new Transport((request, _) => Task.FromResult(IsIdentity(request) ? Identity()
            : IsContext(request) ? Json(HttpStatusCode.OK, Context("id"))
            : request.RequestUri!.Host == AuthoritativeHost ? Json(HttpStatusCode.ServiceUnavailable, "{}") : Json(HttpStatusCode.OK, Summary(1))));
        using var client = Client(transport);

        var reading = await Remote(client).ReadAsync(CancellationToken.None);

        Assert.NotEqual(ReadingConfidence.High, reading.Confidence);
        Assert.All(transport.CloudRequests, request => Assert.Equal(AuthoritativeHost, request.Uri.Host));
        Assert.DoesNotContain(transport.CloudRequests, request => request.Uri.AbsolutePath.EndsWith(":retrieveUserQuota", StringComparison.Ordinal));
        Assert.NotEqual("ok", StateMapper.Map("antigravity-cli", reading).Status);
    }

    [Fact]
    public async Task Remote_UnmeasuredAuthoritativeResponseRemainsUnknownWithoutLegacyGuesses()
    {
        using var transport = new Transport((request, _) => Task.FromResult(IsIdentity(request) ? Identity()
            : IsContext(request) ? Json(HttpStatusCode.OK, Context("projectId")) : Json(HttpStatusCode.OK, Summary(null))));
        using var client = Client(transport);

        var reading = await Remote(client).ReadAsync(CancellationToken.None);
        var mapped = StateMapper.Map("antigravity-cli", reading);

        Assert.NotEqual(ReadingConfidence.High, reading.Confidence);
        Assert.Equal("unknown", mapped.Status);
        Assert.All(mapped.NamedBuckets, bucket => Assert.Null(bucket.Percentage));
        Assert.Equal(2, transport.CloudRequests.Count);
    }

    [Fact]
    public async Task App_VerifiedBackgroundPreferredOverLocalStaleFullQuota()
    {
        var local = Reading(0, accountId: Email, captured: DateTimeOffset.UtcNow.AddHours(-1));
        var authoritative = Reading(52.68f, accountId: Subject);
        var background = new Stub(_ => Task.FromResult(authoritative));

        var result = await new AntigravityAppWithBackgroundSource(new Stub(_ => Task.FromResult(local)), background, new Binding()).ReadAsync(CancellationToken.None);

        Assert.Same(authoritative, result);
        Assert.Equal(1, background.Reads);
        Assert.Equal(52.68f, Assert.Single(result.Usage!.QuotaBuckets!).UsedPercent);
        Assert.Equal(Subject, result.Usage.AccountId);
    }

    [Fact]
    public async Task App_UnavailableBackgroundFallsBackToMeasuredLocalQuota()
    {
        var local = Reading(49.1f, accountId: null);
        var background = new Stub(_ => Task.FromResult(Unavailable()));

        var result = await new AntigravityAppWithBackgroundSource(new Stub(_ => Task.FromResult(local)), background, new Binding()).ReadAsync(CancellationToken.None);

        Assert.NotNull(result.Usage);
        Assert.Equal(49.1f, Assert.Single(result.Usage.QuotaBuckets!).UsedPercent);
        Assert.Equal(1, background.Reads);
        Assert.Equal(local.CapturedAt, result.CapturedAt);
    }

    [Fact]
    public async Task App_ClosedLocalClientUsesVerifiedBackgroundWithoutInventingLocalSnapshot()
    {
        var backgroundReading = Reading(30.38f, accountId: Subject);
        var local = new Stub(_ => Task.FromResult(new ProviderReading(null, null, "Antigravity is not running", DateTimeOffset.UtcNow, ReadingConfidence.Low, ReadingSource.LocalLog)));

        var result = await new AntigravityAppWithBackgroundSource(local, new Stub(_ => Task.FromResult(backgroundReading)), new Binding()).ReadAsync(CancellationToken.None);

        Assert.Same(backgroundReading, result);
        Assert.Equal(Subject, result.Usage!.AccountId);
        Assert.Equal(Email, result.Identity!.Email);
    }

    [Fact]
    public async Task App_LoopbackEmailIdentityIsNotExposedAsACompetingGoogleSubjectId()
    {
        using var handler = new Transport((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/GetUserStatus", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { userStatus = new { email = Email, planStatus = new { planInfo = new { planName = "Pro" } } } }))
            : Json(HttpStatusCode.OK, "{\"response\":" + Summary(0.491) + "}")));
        using var loopback = Client(handler);
        var local = new AntigravityAppSource(new AppSettings(), new Locator(), loopback);
        var wrapper = new AntigravityAppWithBackgroundSource(local, new Stub(_ => Task.FromResult(Unavailable())), new Binding());

        var result = await wrapper.ReadAsync(CancellationToken.None);

        Assert.Equal(Email, result.Identity!.Email);
        Assert.Equal(Email, result.Usage!.AccountEmail);
        Assert.Null(result.Usage.AccountId);
        Assert.Null(StateMapper.Map("antigravity", result).AccountId);
        Assert.NotEmpty(result.Usage.QuotaBuckets!);
    }

    private static AntigravityRemoteSource Remote(HttpClient client) => new(ProviderCatalog.AntigravityCli,
        new AntigravitySessionManager(new Store(), client), () => AntigravityCredentialLocation.Shared, client);
    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    private static bool IsIdentity(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == "https://openidconnect.googleapis.com/v1/userinfo";
    private static bool IsContext(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal);
    private static HttpResponseMessage Json(HttpStatusCode status, string text) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Identity() => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { sub = Subject, email = Email, email_verified = true }));
    private static string Context(string shape)
    {
        object project = shape switch { "id" => new { id = Project }, "projectId" => new { projectId = Project }, _ => Project };
        return JsonSerializer.Serialize(new { cloudaicompanionProject = project, currentTier = new { id = "standard-tier", name = "Pro" }, paidTier = new { id = "paid", name = "Pro" } });
    }
    private static string Summary(double? remaining) => JsonSerializer.Serialize(new
    {
        groups = new[] { new { displayName = "Gemini", buckets = new[] { new { bucketId = "weekly-model", displayName = "Weekly", remainingFraction = remaining, resetTime = "2030-01-07T12:00:00Z" } } } }
    });
    private static ProviderReading Reading(float percentage, string? accountId, DateTimeOffset? captured = null)
    {
        var at = captured ?? DateTimeOffset.UtcNow;
        return new(new UsagePulse("antigravity", at, null, null, null, null, null, null, null, null,
                [new NamedQuotaBucket("weekly", "Weekly", percentage)], Email, accountId),
            new("antigravity", "Antigravity", Email, null, "Pro", "Synthetic source"), "Live", at, ReadingConfidence.High, ReadingSource.Api);
    }
    private static ProviderReading Unavailable() => new(null, null, "Antigravity quota connection unavailable; try again later.", DateTimeOffset.UtcNow, ReadingConfidence.Low, ReadingSource.Api);
    private sealed record Request(Uri Uri, HttpMethod Method, string Agent, string Body);
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public ConcurrentQueue<Request> CloudRequests { get; } = new();
        public int Renewals { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri == "https://oauth2.googleapis.com/token") Renewals++;
            else if (request.RequestUri.Host.EndsWith("cloudcode-pa.googleapis.com", StringComparison.Ordinal))
                CloudRequests.Enqueue(new(request.RequestUri, request.Method, request.Headers.UserAgent.ToString(),
                    request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await send(request, cancellationToken);
        }
    }
    private sealed class Store : IAntigravityCredentialStore
    {
        private readonly AntigravityCredentialSnapshot _snapshot = AntigravityCredentialSnapshot.Parse(AntigravityCredentialLocation.Shared, JsonSerializer.Serialize(new
        {
            token = new { access_token = "SYNTHETIC_AUTHORITY_ACCESS", expiry = DateTimeOffset.UtcNow.AddHours(2).ToString("O") },
            id_token = Token()
        }));
        public AntigravityCredentialSnapshot? Read(AntigravityCredentialLocation location) => location.Key == _snapshot.Location.Key ? _snapshot : null;
        public bool TrySave(AntigravityCredentialSnapshot expected, AntigravityCredentialSnapshot replacement) => throw new InvalidOperationException("This valid synthetic session must not be renewed.");
        private static string Token()
        {
            static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Encode("{\"alg\":\"none\"}") + "." + Encode(JsonSerializer.Serialize(new { sub = Subject, email = Email, aud = AntigravitySessionManager.ConsumerClientId })) + ".synthetic";
        }
    }
    private sealed class Stub(Func<CancellationToken, Task<ProviderReading>> read) : ISignalSource
    {
        public int Reads { get; private set; }
        public ProviderProfile Profile => ProviderCatalog.Antigravity;
        public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken) { Reads++; return read(cancellationToken); }
    }
    private sealed class Binding : IAntigravityAccountBinding
    {
        public void SetFromLocal(string email) { }
        public bool MatchOrBindRemote(string email) => true;
    }
    private sealed class Locator : IAntigravityProcessLocator
    {
        public AntigravityProcessInfo? LocateProcess(AppSettings? settings) => new(424242, "SYNTHETIC_CSRF_FOR_FAKE_HANDLER", [12345]);
    }
}
