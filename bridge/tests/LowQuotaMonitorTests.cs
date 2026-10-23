using System.Text.Json;
using Costats.Bridge.Notifications;

namespace Costats.Bridge.Tests;

public sealed class LowQuotaMonitorTests
{
    private static readonly NotificationSettingsDto Default = new(true, 10, 5);

    [Fact]
    public async Task FirstLowReadingWarnsOnce_ThenEscalatesToCriticalOnce()
    {
        using var fixture = new Fixture();
        var warning = Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
        Assert.Contains("low", warning.Title);
        Assert.Contains("8% remaining", warning.Message);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
        fixture.Clock.AdvanceMinutes(1);
        var critical = Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 97)));
        Assert.Contains("critical", critical.Title);
        Assert.Contains("3% remaining", critical.Message);
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
    }

    [Fact]
    public async Task JumpFromHealthyToCriticalSendsOnlyHighestSeverity()
    {
        using var fixture = new Fixture();
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 85)));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Contains("critical", Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 96))).Title);
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 93)));
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("expired")]
    [InlineData("unknown")]
    [InlineData("error")]
    public async Task NonSuccessfulReadingsNeverNotifyOrCreateHistory(string status)
    {
        using var fixture = new Fixture();
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 98) with { Status = status }));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task UnknownInvalidNonFiniteAndOutOfRangePercentagesAreIgnored()
    {
        using var fixture = new Fixture();
        foreach (var value in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 101 })
            Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, value)));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task EstimatedLocalLogsAreExcludedEvenWithOkStatusAndMeasuredPercentage()
    {
        using var fixture = new Fixture();
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 98) with { SourceLabel = "LOCAL LOGS" }));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task RefreshDemoDisabledStaleAndFutureSnapshotsAreIgnored()
    {
        using var fixture = new Fixture();
        var provider = Provider(fixture.Clock.Now, 98);
        Assert.Empty(await fixture.Monitor.EvaluateAsync(State(provider) with { IsRefreshing = true }, Default));
        Assert.Empty(await fixture.Monitor.EvaluateAsync(State(provider) with { Mode = "demo" }, Default));
        Assert.Empty(await fixture.Monitor.EvaluateAsync(State(provider), Default with { Enabled = false }));
        Assert.Empty(await fixture.Evaluate(provider with { LastUpdated = fixture.Clock.Now.AddMinutes(-16).ToString("O") }));
        Assert.Empty(await fixture.Evaluate(provider with { LastUpdated = fixture.Clock.Now.AddMinutes(2).ToString("O") }));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task RestartDoesNotRepeatAndHistoryContainsOnlyHashedIdentities()
    {
        using var fixture = new Fixture();
        var provider = Provider(fixture.Clock.Now, 98);
        Assert.Single(await fixture.Evaluate(provider));
        var history = await File.ReadAllTextAsync(fixture.Path);
        Assert.DoesNotContain(provider.AccountId!, history);
        Assert.DoesNotContain(provider.AccountEmail!, history);
        Assert.DoesNotContain(provider.DisplayName, history);
        Assert.Empty(await new LowQuotaMonitor(fixture.Path, fixture.Clock).EvaluateAsync(State(provider), Default));
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!));
    }

    [Fact]
    public async Task IdenticalFreshRepublishDoesNotWriteOrRearm()
    {
        using var fixture = new Fixture();
        var provider = Provider(fixture.Clock.Now, 98);
        Assert.Single(await fixture.Evaluate(provider));
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.Path, timestamp);
        Assert.Empty(await fixture.Evaluate(provider));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.Path));
    }

    [Fact]
    public async Task SharedKnownAccountDeduplicatesAppCliAndHiddenCounterpart()
    {
        using var fixture = new Fixture();
        var app = Provider(fixture.Clock.Now, 92);
        var cli = app with { Id = "codex-cli", DisplayName = "Codex CLI" };
        Assert.Single(await fixture.Evaluate(app, cli));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(cli with { LastUpdated = fixture.Clock.Now.ToString("O") }));
    }

    [Fact]
    public async Task KnownDifferentIdsRemainDistinctEvenWhenEmailMatches()
    {
        using var fixture = new Fixture();
        var first = Provider(fixture.Clock.Now, 98);
        var other = first with { Id = "codex-cli", DisplayName = "Codex CLI", AccountId = "synthetic-other-account" };
        Assert.Equal(2, (await fixture.Evaluate(first, other)).Count);
        Assert.Empty(await fixture.Evaluate(first, other));
    }

    [Fact]
    public async Task EmailAliasPreservesSharedAccountDedupAfterRestartAndAppHidden()
    {
        using var fixture = new Fixture();
        var app = Provider(fixture.Clock.Now, 98);
        var cli = app with { Id = "codex-cli", DisplayName = "Codex CLI", AccountId = null };
        Assert.Single(await fixture.Evaluate(app, cli));
        fixture.Clock.AdvanceMinutes(1);
        cli = cli with { LastUpdated = fixture.Clock.Now.ToString("O") };
        Assert.Empty(await new LowQuotaMonitor(fixture.Path, fixture.Clock).EvaluateAsync(State(cli), Default));
    }

    [Fact]
    public async Task StrongerIdentityMigratesEarlierEmailOnlyAlertWithoutReplay()
    {
        using var fixture = new Fixture();
        var emailOnly = Provider(fixture.Clock.Now, 98) with { AccountId = null };
        Assert.Single(await fixture.Evaluate(emailOnly));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 98)));
    }

    [Fact]
    public async Task FamiliesAccountsAndQuotaWindowsAreSeparate_AccountAlertsAreGrouped()
    {
        using var fixture = new Fixture();
        var provider = Provider(fixture.Clock.Now, 92) with
        {
            WeeklyProgress = 98,
            NamedBuckets = [new("model-a", "Pro", 93, null, null), new("model-b", "Flash", 99, null, null)]
        };
        var alert = Assert.Single(await fixture.Evaluate(provider));
        Assert.Contains("critical", alert.Title);
        Assert.Contains("+1 other limits", alert.Message);
        Assert.True(alert.Message.Length <= 420);
        fixture.Clock.AdvanceMinutes(1);
        Assert.Single(await fixture.Evaluate(provider with { Family = "antigravity", Id = "antigravity", LastUpdated = fixture.Clock.Now.ToString("O") }));
        Assert.Single(await fixture.Evaluate(provider with { AccountId = "another-synthetic-account", LastUpdated = fixture.Clock.Now.ToString("O") }));
    }

    [Fact]
    public async Task DistinctNamedBucketsAlertSeparately_ButSameBucketAcrossClientsDeduplicates()
    {
        using var fixture = new Fixture();
        var provider = Provider(fixture.Clock.Now, null) with { NamedBuckets = [new("model-a", "Model A", 92, null, null)] };
        var cli = provider with { Id = "codex-cli", DisplayName = "Codex CLI" };
        Assert.Single(await fixture.Evaluate(provider, cli));
        fixture.Clock.AdvanceMinutes(1);
        var alert = Assert.Single(await fixture.Evaluate(provider with { LastUpdated = fixture.Clock.Now.ToString("O"), NamedBuckets = [new("model-b", "Model B", 92, null, null)] }));
        Assert.Contains("Model B", alert.Message);
    }

    [Fact]
    public async Task MissingResetRequiresTwoDistinctRecoveredReadingsBeforeRearm()
    {
        using var fixture = new Fixture();
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
        fixture.Clock.AdvanceMinutes(1);
        var recovery = Provider(fixture.Clock.Now, 87); //13% remaining, above warning+2.
        Assert.Empty(await fixture.Evaluate(recovery));
        Assert.Empty(await fixture.Evaluate(recovery)); //Republishing one reading is not sustained recovery.
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 87)));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 87)));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
    }

    [Fact]
    public async Task StaleExpiredAndRefreshingRecoveryCannotRearm()
    {
        using var fixture = new Fixture();
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
        fixture.Clock.AdvanceMinutes(1);
        var recovery = Provider(fixture.Clock.Now, 50);
        Assert.Empty(await fixture.Evaluate(recovery with { Status = "expired" }));
        Assert.Empty(await fixture.Monitor.EvaluateAsync(State(recovery) with { IsRefreshing = true }, Default));
        fixture.Clock.AdvanceMinutes(20);
        Assert.Empty(await fixture.Evaluate(recovery));
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 92)));
    }

    [Fact]
    public async Task ResetTimestampJitterDoesNotRearm_ButVerifiedNextCycleDoes()
    {
        using var fixture = new Fixture();
        var oldReset = fixture.Clock.Now.AddHours(1);
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 96, reset: oldReset)));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 96, reset: oldReset.AddSeconds(30))));
        fixture.Clock.AdvanceMinutes(60);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 96, reset: oldReset.AddSeconds(40))));
        fixture.Clock.AdvanceMinutes(1);
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 96, reset: oldReset.AddHours(5))));
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 96, reset: oldReset.AddHours(5))));
    }

    [Fact]
    public async Task HealthyReadingsWithKnownUnexpiredResetDoNotRearm()
    {
        using var fixture = new Fixture();
        var reset = fixture.Clock.Now.AddHours(5);
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 92, reset: reset)));
        for (var i = 0; i < 2; i++)
        {
            fixture.Clock.AdvanceMinutes(1);
            Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 50, reset: reset)));
        }
        fixture.Clock.AdvanceMinutes(1);
        Assert.Empty(await fixture.Evaluate(Provider(fixture.Clock.Now, 92, reset: reset)));
    }

    [Fact]
    public async Task ThresholdChangeCanNotifyNewBandOnce_UnchangedCriticalIsNotReissued()
    {
        using var fixture = new Fixture();
        var provider = Provider(fixture.Clock.Now, 92);
        Assert.Single(await fixture.Evaluate(provider));
        var changed = Default with { WarningPercent = 12 };
        Assert.Single(await fixture.Monitor.EvaluateAsync(State(provider), changed));
        Assert.Empty(await fixture.Monitor.EvaluateAsync(State(provider), changed));
        fixture.Clock.AdvanceMinutes(1);
        provider = Provider(fixture.Clock.Now, 97);
        Assert.Single(await fixture.Monitor.EvaluateAsync(State(provider), changed));
        Assert.Empty(await fixture.Monitor.EvaluateAsync(State(provider), changed with { WarningPercent = 20 }));
    }

    [Fact]
    public async Task ConcurrentCallsSerialize_AndCanceledCallReleasesGate()
    {
        using var fixture = new Fixture();
        var state = State(Provider(fixture.Clock.Now, 98));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Monitor.EvaluateAsync(state, Default, cancellation.Token));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Monitor.EvaluateAsync(state, Default)));
        Assert.Equal(1, results.Sum(alerts => alerts.Count));
    }

    [Fact]
    public async Task PersistenceFailureIsGeneric_DoesNotConsumeAlert_AndGateRecovers()
    {
        using var temporary = new TemporaryDirectory();
        var blocked = System.IO.Path.Combine(temporary.Path, "SYNTHETIC_PRIVATE_LOCATION");
        File.WriteAllText(blocked, "blocking file");
        var path = System.IO.Path.Combine(blocked, "history.json");
        var clock = new Clock();
        var monitor = new LowQuotaMonitor(path, clock);
        var state = State(Provider(clock.Now, 98));
        var error = await Assert.ThrowsAsync<IOException>(() => monitor.EvaluateAsync(state, Default));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_LOCATION", error.ToString());
        File.Delete(blocked);
        Assert.Single(await monitor.EvaluateAsync(state, Default));
        Assert.Empty(await monitor.EvaluateAsync(state, Default));
    }

    [Fact]
    public async Task AlertTextIsBoundedAndDoesNotContainEmailsAccountIdsOrTokenLikeLabels()
    {
        using var fixture = new Fixture();
        var provider = Provider(fixture.Clock.Now, null) with
        {
            DisplayName = "Codex synthetic-account-private", AccountId = "synthetic-account-private",
            NamedBuckets = [new("model-a", "SYNTHETIC_ACCESS_TOKEN_VALUE", 97.25, fixture.Clock.Now.AddHours(1).ToString("O"), null)]
        };
        var alert = Assert.Single(await fixture.Evaluate(provider));
        Assert.DoesNotContain(provider.AccountId, alert.Title + alert.Message);
        Assert.DoesNotContain(provider.AccountEmail!, alert.Title + alert.Message);
        Assert.DoesNotContain("SYNTHETIC_ACCESS_TOKEN_VALUE", alert.Title + alert.Message);
        Assert.Contains("2.75% remaining", alert.Message);
        Assert.Contains("UTC", alert.Message);
        Assert.True(alert.Title.Length <= 80);
        Assert.True(alert.Message.Length <= 420);
    }

    [Fact]
    public async Task HistoryPrunesOldAccountsAndBoundsStoredEntries()
    {
        using var fixture = new Fixture();
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 98)));
        fixture.Clock.AdvanceMinutes(46 * 24 * 60);
        Assert.Single(await fixture.Evaluate(Provider(fixture.Clock.Now, 98) with { AccountId = "new-account", AccountEmail = "new@example.test" }));
        using (var pruned = JsonDocument.Parse(File.ReadAllText(fixture.Path)))
            Assert.Single(pruned.RootElement.GetProperty("entries").EnumerateObject());
        var many = Enumerable.Range(0, 1030).Select(i => Provider(fixture.Clock.Now, 98) with { AccountId = "synthetic-account-" + i, AccountEmail = null }).ToArray();
        await fixture.Evaluate(many);
        Assert.Empty(await fixture.Evaluate(many));
        using var bounded = JsonDocument.Parse(File.ReadAllText(fixture.Path));
        Assert.InRange(bounded.RootElement.GetProperty("entries").EnumerateObject().Count(), 1, 1024);
        Assert.True(new FileInfo(fixture.Path).Length < 1024 * 1024);
    }

    private static DashboardDto State(params ProviderDto[] providers) => new(1, "live", providers, false, null, []);
    private static ProviderDto Provider(DateTimeOffset captured, double? used, DateTimeOffset? reset = null) => new(
        "codex", "Codex App", "codex", "Plus", "quota-private@example.test", "synthetic-account-private", "Codex App", "ok", null, false, null,
        used, null, reset?.ToString("O"), null, [], captured.ToString("O"), null, null);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void AdvanceMinutes(int minutes) => Now = Now.AddMinutes(minutes);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        public string Path { get; }
        public Clock Clock { get; } = new();
        public LowQuotaMonitor Monitor { get; }
        public Fixture() { Path = System.IO.Path.Combine(_temporary.Path, "history", "quota-alerts.json"); Monitor = new LowQuotaMonitor(Path, Clock); }
        public Task<IReadOnlyList<QuotaAlert>> Evaluate(params ProviderDto[] providers) => Monitor.EvaluateAsync(State(providers), Default);
        public void Dispose() => _temporary.Dispose();
    }
}
