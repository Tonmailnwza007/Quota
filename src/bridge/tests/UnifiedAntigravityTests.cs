using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Costats.Bridge.Antigravity;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Costats.Bridge.Tests;

public sealed class UnifiedAntigravityTests
{
    [Fact]
    public async Task UnifiedReader_RequeriesServerAndFollowsVerifiedAccountSwitch_WithoutAnyClientSource()
    {
        var store = new Store();
        using var transport = new Transport();
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(new AppSettings(), store, http);
        Assert.Equal("antigravity", source.Profile.ProviderId);
        Assert.Equal("Antigravity", source.Profile.DisplayName);

        var first = await source.ReadAsync(CancellationToken.None);
        transport.Remaining = .25;
        var second = await source.ReadAsync(CancellationToken.None);
        Assert.InRange(Used(first), 59.99f, 60.01f);
        Assert.Equal(75f, Used(second));
        Assert.Equal(2, transport.QuotaReads); // No local/cached quota response.
        Assert.Equal(1, transport.IdentityReads); // Only identity verification can be reused.

        store.Current = Credential("beta");
        transport.Remaining = .8;
        var switched = await source.ReadAsync(CancellationToken.None);
        Assert.Equal("beta@example.test", switched.Identity!.Email);
        Assert.Equal("google-beta", switched.Usage!.AccountId);
        Assert.NotEqual(first.Usage!.AccountId, switched.Usage.AccountId);
        Assert.InRange(Used(switched), 19.99f, 20.01f);
        Assert.Equal(2, transport.IdentityReads);
        var dto = StateMapper.Map("antigravity", switched);
        Assert.Equal("Antigravity", dto.DisplayName);
        Assert.Equal("Saved sign-in", dto.SourceLabel);
        Assert.False(dto.IsSharedAccountQuota);
        Assert.Null(dto.SharedNotice);
    }

    [Fact]
    public async Task FailureOrDeletedSignInAfterSuccess_DoesNotKeepOldQuotaOrRestoreDeletedGrant()
    {
        var store = new Store();
        using var transport = new Transport();
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(new AppSettings(), store, http);
        Assert.Equal("ok", StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None)).Status);

        transport.Offline = true;
        var offline = StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None));
        Assert.Equal("offline", offline.Status);
        Assert.Empty(offline.NamedBuckets);
        Assert.Null(offline.SessionProgress);
        Assert.Null(offline.WeeklyProgress);

        transport.Offline = false;
        transport.Remaining = .2;
        Assert.Equal(80f, Used(await source.ReadAsync(CancellationToken.None)));
        store.Current = null;
        var signedOut = StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None));
        Assert.Equal("expired", signedOut.Status);
        Assert.Null(signedOut.AccountEmail);
        Assert.Empty(signedOut.NamedBuckets);
        Assert.Equal(0, store.Saves);
        Assert.Equal(2, transport.QuotaReads);
        Assert.Null(store.Current);
    }

    [Theory]
    [InlineData("choose")]
    [InlineData("custom")]
    public async Task UnresolvedAccountSourceStopsBeforeReadingAnyCredentialOrNetwork(string mode)
    {
        var store = new Store();
        using var transport = new Transport();
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(new AppSettings { AntigravitySource = mode }, store, http);
        var dto = StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None));
        Assert.Equal("unknown", dto.Status);
        Assert.Contains("Choose the Antigravity account source", dto.StatusMessage);
        Assert.Empty(dto.NamedBuckets);
        Assert.Empty(store.Reads);
        Assert.Equal(0, transport.TotalReads);
    }

    [Fact]
    public async Task ExplicitMissingCustomFileNeverFallsBackToValidSharedGrant()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "not-signed-in.json");
        var settings = new AppSettings { AntigravitySource = "custom", AntigravityAuthPath = file, AntigravityCredentialTarget = "synthetic:other" };
        var store = new Store();
        using var transport = new Transport();
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(settings, store, http);
        var dto = StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None));
        Assert.Equal("expired", dto.Status);
        Assert.Null(dto.AccountEmail);
        Assert.Empty(dto.NamedBuckets);
        Assert.Equal(new AntigravityCredentialLocation(file, "").Key, Assert.Single(store.Reads));
        Assert.Equal(0, transport.TotalReads);
    }

    [Fact]
    public async Task SignOutDuringQuotaRequest_DiscardsInFlightAccountAndQuota()
    {
        var store = new Store();
        using var transport = new Transport { OnQuota = () => store.Current = null };
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(new AppSettings(), store, http);
        var dto = StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None));
        Assert.NotEqual("ok", dto.Status);
        Assert.Null(dto.AccountEmail);
        Assert.Empty(dto.NamedBuckets);
        Assert.Contains("changed during refresh", dto.StatusMessage);
    }

    [Fact]
    public async Task FailedRequestDuringSignOut_DropsPreviousIdentity()
    {
        var store = new Store();
        using var transport = new Transport { Offline = true, OnContext = () => store.Current = null };
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(new AppSettings(), store, http);
        var dto = StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None));
        Assert.NotEqual("ok", dto.Status);
        Assert.Null(dto.AccountEmail);
        Assert.Empty(dto.NamedBuckets);
        Assert.Contains("changed during refresh", dto.StatusMessage);
    }

    [Fact]
    public async Task FailedRequestDuringSourceSwitch_DropsPreviousIdentity()
    {
        var store = new Store();
        var settings = new AppSettings();
        using var transport = new Transport { Offline = true, OnContext = () => { settings.AntigravitySource = "custom"; settings.AntigravityCredentialTarget = "synthetic:other"; } };
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(settings, store, http);
        var dto = StateMapper.Map("antigravity", await source.ReadAsync(CancellationToken.None));
        Assert.NotEqual("ok", dto.Status);
        Assert.Null(dto.AccountEmail);
        Assert.Empty(dto.NamedBuckets);
    }

    [Fact]
    public async Task ScheduledRefreshUpdatesQuotaWhileThereIsNoRendererOrClientReader()
    {
        var store = new Store();
        using var transport = new Transport { AdvanceEachReading = true };
        using var http = new HttpClient(transport);
        var source = SourceFactory.CreateAntigravity(new AppSettings(), store, http);
        var broadcaster = new PulseBroadcaster();
        var updates = new Updates();
        using var subscription = broadcaster.Subscribe(updates);
        using var pulse = new PulseOrchestrator([source], new SourceSelector(NullLogger<SourceSelector>.Instance), new SystemClock(),
            broadcaster, new NullSnapshotWriter(), Options.Create(new PulseOptions { RefreshInterval = TimeSpan.FromMilliseconds(50) }),
            NullLogger<PulseOrchestrator>.Instance, new AppSettings());
        await pulse.StartAsync(CancellationToken.None);
        try
        {
            var initial = await updates.Values.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var scheduled = await updates.Values.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(RefreshTrigger.Initial, initial.Trigger);
            Assert.Equal(RefreshTrigger.Scheduled, scheduled.Trigger);
            Assert.NotEqual(Used(initial.Providers["antigravity"]), Used(scheduled.Providers["antigravity"]));
            Assert.Single(scheduled.Providers);
            Assert.True(transport.QuotaReads >= 2);
        }
        finally { await pulse.StopAsync(CancellationToken.None); }
    }

    private static float Used(ProviderReading reading) => Assert.Single(reading.Usage!.QuotaBuckets!).UsedPercent!.Value;
    private static AntigravityCredentialSnapshot Credential(string name) => AntigravityCredentialSnapshot.Parse(AntigravityCredentialLocation.Shared,
        JsonSerializer.Serialize(new { token = new { access_token = name, expiry = DateTimeOffset.UtcNow.AddHours(1) },
            id_token = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { email = name + "@example.test", sub = "google-" + name }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic" }));
    private sealed class Store : IAntigravityCredentialStore
    {
        public AntigravityCredentialSnapshot? Current = Credential("alpha");
        public List<string> Reads { get; } = [];
        public int Saves;
        public AntigravityCredentialSnapshot? Read(AntigravityCredentialLocation location)
        { Reads.Add(location.Key); return Current?.Location.Key == location.Key ? Current : null; }
        public bool TrySave(AntigravityCredentialSnapshot expected, AntigravityCredentialSnapshot replacement) { Saves++; throw new InvalidOperationException("Valid synthetic credentials must not renew."); }
    }
    private sealed class Transport : HttpMessageHandler
    {
        public double Remaining = .4;
        public bool Offline;
        public bool AdvanceEachReading;
        public Action? OnQuota;
        public Action? OnContext;
        public int IdentityReads, QuotaReads, TotalReads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            TotalReads++;
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
            Assert.False(request.RequestUri.IsLoopback);
            if (request.RequestUri.Host == "openidconnect.googleapis.com")
            {
                IdentityReads++;
                var name = request.Headers.Authorization!.Parameter!;
                return Json(new { email = name + "@example.test", sub = "google-" + name, email_verified = true });
            }
            Assert.Equal(AntigravityRemoteSource.OfficialQuotaHost, request.RequestUri.Host);
            Assert.Equal(AntigravityRemoteSource.QuotaUserAgent, request.Headers.UserAgent.ToString());
            if (request.RequestUri.AbsolutePath.EndsWith(":loadCodeAssist", StringComparison.Ordinal))
            {
                OnContext?.Invoke();
                if (Offline) throw new HttpRequestException("Synthetic offline service");
                return Json(new { cloudaicompanionProject = "synthetic-project", currentTier = new { id = "pro", name = "Pro" } });
            }
            if (Offline) throw new HttpRequestException("Synthetic offline service");
            Assert.EndsWith(":retrieveUserQuotaSummary", request.RequestUri.AbsolutePath);
            QuotaReads++;
            OnQuota?.Invoke();
            return Json(new { buckets = new[] { new { bucketId = "weekly", displayName = "Weekly", remainingFraction = AdvanceEachReading ? .9 - .1 * QuotaReads : Remaining } } });
        }
        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }
    private sealed class Updates : IObserver<PulseState>
    {
        public Channel<PulseState> Values { get; } = Channel.CreateUnbounded<PulseState>();
        public void OnNext(PulseState state) { if (!state.IsRefreshing) Values.Writer.TryWrite(state); }
        public void OnError(Exception error) => Values.Writer.TryComplete(error);
        public void OnCompleted() => Values.Writer.TryComplete();
    }
}
