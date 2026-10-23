using System.Text.Json;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Costats.Bridge.Tests;

public sealed class PreferencesAndSharedQuotaTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task LegacySettings_DefaultOptionalDetailsAndAdjustableAlerts_ThenRoundTrip()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{\"enabled\":{\"claude\":false},\"refreshMinutes\":3}");
        var store = new ModernSettingsStore(path);
        var loaded = await store.LoadAsync();
        var defaults = SettingsValidation.Snapshot(loaded);
        Assert.False(defaults.ShowUsageDetails);
        Assert.Equal(new NotificationSettingsDto(true, 10, 5), defaults.Notifications);
        var changed = SettingsValidation.ValidatePatch(Json("{\"showUsageDetails\":true,\"notifications\":{\"enabled\":false,\"warningPercent\":22,\"criticalPercent\":7}}"), defaults);
        await store.SaveAsync(changed);
        var restored = SettingsValidation.Snapshot(await store.LoadAsync());
        Assert.True(restored.ShowUsageDetails);
        Assert.Equal(new NotificationSettingsDto(false, 22, 7), restored.Notifications);
        Assert.False(restored.Enabled["claude"]);
        Assert.Equal(3, restored.RefreshMinutes);
    }

    [Theory]
    [InlineData("{\"notifications\":null}")]
    [InlineData("{\"notifications\":[]}")]
    [InlineData("{\"notifications\":{\"warningPercent\":5,\"criticalPercent\":5}}")]
    [InlineData("{\"notifications\":{\"warningPercent\":1,\"criticalPercent\":2}}")]
    [InlineData("{\"notifications\":{\"warningPercent\":101}}")]
    [InlineData("{\"notifications\":{\"criticalPercent\":-1}}")]
    [InlineData("{\"notifications\":{\"warningPercent\":10.5}}")]
    [InlineData("{\"notifications\":{\"enabled\":\"true\"}}")]
    [InlineData("{\"notifications\":{\"extra\":1}}")]
    [InlineData("{\"notifications\":{\"warningPercent\":20,\"warningPercent\":30}}")]
    [InlineData("{\"showUsageDetails\":1}")]
    public void InvalidAlertSettingsRejectedWithoutMutatingCurrentPreferences(string patch)
    {
        var current = SettingsValidation.Snapshot(new AppSettings());
        Assert.Throws<InvalidSettingsException>(() => SettingsValidation.ValidatePatch(Json(patch), current));
        Assert.False(current.ShowUsageDetails);
        Assert.Equal(new NotificationSettingsDto(), current.Notifications);
    }

    [Fact]
    public void PartialNotificationPatchPreservesOtherValues_AndSupportsZeroCritical()
    {
        var initial = SettingsValidation.Snapshot(new AppSettings());
        var thresholds = SettingsValidation.ValidatePatch(Json("{\"notifications\":{\"warningPercent\":25,\"criticalPercent\":0}}"), initial);
        var disabled = SettingsValidation.ValidatePatch(Json("{\"notifications\":{\"enabled\":false}}"), thresholds);
        Assert.Equal(new NotificationSettingsDto(false, 25, 0), disabled.Notifications);
    }

    [Theory]
    [InlineData("a@example.test", null, "A@EXAMPLE.TEST", null, true)]
    [InlineData("a@example.test", null, "b@example.test", null, false)]
    [InlineData("a@example.test", "google-a", "a@example.test", "google-b", false)]
    [InlineData("a@example.test", "google-a", "alias@example.test", "google-a", true)]
    [InlineData(null, null, null, null, false)]
    [InlineData(" a@example.test ", null, "a@example.test", "google-a", true)]
    public async Task AntigravitySharedQuotaFollowsIdentity_NotAnySpecificUser(string? appEmail, string? appId, string? cliEmail, string? cliId, bool expected)
    {
        var app = new MutableSource("antigravity", appEmail, appId);
        var cli = new MutableSource("antigravity-cli", cliEmail, cliId);
        using var pulse = Orchestrator([app, cli], new AppSettings());
        var capture = new Capture();
        using var subscription = pulse.PulseStream.Subscribe(capture);
        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        Assert.All(capture.Value!.Providers.Values, reading => Assert.Equal(expected, reading.Usage!.IsSharedAccountQuota));
    }

    [Fact]
    public async Task SharedFlagClearsWhenIdentityChangesOrCounterpartHidden()
    {
        var settings = new AppSettings();
        var app = new MutableSource("antigravity", "a@example.test", "google-a");
        var cli = new MutableSource("antigravity-cli", "a@example.test", "google-a");
        using var pulse = Orchestrator([app, cli], settings);
        var capture = new Capture();
        using var subscription = pulse.PulseStream.Subscribe(capture);
        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        Assert.All(capture.Value!.Providers.Values, reading => Assert.True(reading.Usage!.IsSharedAccountQuota));
        cli.Email = "b@example.test"; cli.AccountId = "google-b";
        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        Assert.All(capture.Value!.Providers.Values, reading => Assert.False(reading.Usage!.IsSharedAccountQuota));
        cli.Email = app.Email; cli.AccountId = app.AccountId;
        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        settings.AntigravityCliEnabled = false;
        pulse.NotifySettingsChanged();
        Assert.Single(capture.Value!.Providers);
        Assert.False(capture.Value.Providers["antigravity"].Usage!.IsSharedAccountQuota);
    }

    public static IEnumerable<object[]> UnusableSharedQuotaReadings()
    {
        foreach (var id in new[] { "antigravity", "antigravity-cli" })
            foreach (var failure in new[] { "missing-usage", "unknown-quota", "missing-buckets", "low-confidence", "medium-confidence", "nan", "infinity" })
                yield return [id, failure];
    }

    [Theory]
    [MemberData(nameof(UnusableSharedQuotaReadings))]
    public async Task AntigravitySharedRequiresBothCurrentHighConfidenceMeasuredReadings_AndRecovers(string failingId, string failure)
    {
        var app = new MutableSource("antigravity", "same-account@example.test", "google-same-account");
        var cli = new MutableSource("antigravity-cli", "same-account@example.test", "google-same-account");
        using var pulse = Orchestrator([app, cli], new AppSettings());
        var capture = new Capture();
        using var subscription = pulse.PulseStream.Subscribe(capture);
        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        Assert.All(capture.Value!.Providers.Values, reading => Assert.True(reading.Usage!.IsSharedAccountQuota));

        var failing = failingId == "antigravity" ? app : cli;
        failing.ReadingOverride = reading =>
        {
            // Retained matching identity and even an old shared flag must not prove current quota availability.
            var priorUsage = reading.Usage! with { IsSharedAccountQuota = true, StatusMessage = "Shared account quota from an earlier successful reading" };
            return failure switch
            {
                "missing-usage" => reading with { Usage = null, StatusSummary = "Saved session unavailable", Confidence = ReadingConfidence.Low },
                "unknown-quota" => reading with { Usage = priorUsage with { QuotaBuckets = [new NamedQuotaBucket("test-quota", "Test quota", null)] } },
                "missing-buckets" => reading with { Usage = priorUsage with { QuotaBuckets = null } },
                "low-confidence" => reading with { Usage = priorUsage, Confidence = ReadingConfidence.Low },
                "medium-confidence" => reading with { Usage = priorUsage, Confidence = ReadingConfidence.Medium },
                "nan" => reading with { Usage = priorUsage with { QuotaBuckets = [new NamedQuotaBucket("test-quota", "Test quota", float.NaN)] } },
                "infinity" => reading with { Usage = priorUsage with { QuotaBuckets = [new NamedQuotaBucket("test-quota", "Test quota", float.PositiveInfinity)] } },
                _ => throw new InvalidOperationException("Unknown synthetic regression case.")
            };
        };
        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

        Assert.Equal("same-account@example.test", capture.Value!.Providers[failingId].Identity!.Email);
        Assert.All(capture.Value.Providers, pair =>
        {
            Assert.False(pair.Value.Usage?.IsSharedAccountQuota ?? false);
            Assert.Null(pair.Value.Usage?.StatusMessage);
            var dto = StateMapper.Map(pair.Key, pair.Value);
            Assert.False(dto.IsSharedAccountQuota);
            Assert.Null(dto.SharedNotice);
        });

        failing.ReadingOverride = null;
        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);
        Assert.All(capture.Value!.Providers.Values, reading => Assert.True(reading.Usage!.IsSharedAccountQuota));
    }

    [Fact]
    public async Task AntigravityConfirmedZeroUsedRemainsMeasuredAndCanShareQuota()
    {
        var app = new MutableSource("antigravity", "same-account@example.test", "google-same-account");
        var cli = new MutableSource("antigravity-cli", "same-account@example.test", "google-same-account");
        ProviderReading ZeroUsed(ProviderReading reading) => reading with
        {
            Usage = reading.Usage! with { QuotaBuckets = [new NamedQuotaBucket("test-quota", "Test quota", 0f)] }
        };
        app.ReadingOverride = ZeroUsed;
        cli.ReadingOverride = ZeroUsed;
        using var pulse = Orchestrator([app, cli], new AppSettings());
        var capture = new Capture();
        using var subscription = pulse.PulseStream.Subscribe(capture);

        await pulse.RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None);

        Assert.All(capture.Value!.Providers.Values, reading =>
        {
            Assert.Equal(0f, Assert.Single(reading.Usage!.QuotaBuckets!).UsedPercent);
            Assert.True(reading.Usage.IsSharedAccountQuota);
        });
    }

    private static PulseOrchestrator Orchestrator(ISignalSource[] sources, AppSettings settings) => new(sources,
        new SourceSelector(NullLogger<SourceSelector>.Instance), new SystemClock(), new PulseBroadcaster(), new NullSnapshotWriter(),
        Options.Create(new PulseOptions()), NullLogger<PulseOrchestrator>.Instance, settings);
    private sealed class Capture : IObserver<PulseState>
    {
        public PulseState? Value;
        public void OnNext(PulseState value) => Value = value;
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }
    private sealed class MutableSource(string id, string? email, string? accountId) : ISignalSource
    {
        public string? Email = email, AccountId = accountId;
        public Func<ProviderReading, ProviderReading>? ReadingOverride { get; set; }
        public ProviderProfile Profile { get; } = new(id, id, "#ffffff");
        public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var reading = new ProviderReading(new UsagePulse(id, now, null, null, null, null, null, null, null, null,
                [new NamedQuotaBucket("test-quota", "Test quota", 95)], Email, AccountId),
                new IdentityCard(id, id, Email, null, null, "Synthetic"), "Live", now, ReadingConfidence.High, ReadingSource.Api);
            return Task.FromResult(ReadingOverride?.Invoke(reading) ?? reading);
        }
    }
}
