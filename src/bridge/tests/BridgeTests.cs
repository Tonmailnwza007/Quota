using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using Costats.Bridge.Antigravity;

namespace Costats.Bridge.Tests;

public sealed class BridgeTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task ClaudeReadOnly_ExpiredCredentialDoesNotLaunchOwnerClientOrUseDiskCache()
    {
        using var temp = new TemporaryDirectory();
        var credentialFile = Path.Combine(temp.Path, ".credentials.json");
        const string original = "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-expired\",\"expiresAt\":1}}";
        await File.WriteAllTextAsync(credentialFile, original);
        var launchCount = 0;
        var transport = new SyntheticClaudeTransport();
        var cache = Path.Combine(temp.Path, "cache");
        using var client = new HttpClient(transport) { BaseAddress = new Uri("https://example.test") };
        using var fetcher = new ClaudeOAuthUsageFetcher(temp.Path, allowDelegatedRefresh: false, useDiskCache: false,
            httpClient: client, delegatedRefresh: _ => { launchCount++; return Task.CompletedTask; }, cacheDirectory: cache);
        Assert.Null(await fetcher.FetchAsync(CancellationToken.None));
        Assert.Equal(0, launchCount);
        Assert.Equal(0, transport.RequestCount);
        Assert.False(Directory.Exists(cache));
        Assert.Equal(original, await File.ReadAllTextAsync(credentialFile));
    }

    [Fact]
    public async Task ClaudeReadOnly_FreshApiQuotaUsesMemoryWithoutWritingLegacyCache()
    {
        using var temp = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".credentials.json"), "{\"claudeAiOauth\":{\"accessToken\":\"synthetic-current\",\"expiresAt\":4102444800000}}");
        var transport = new SyntheticClaudeTransport();
        var cache = Path.Combine(temp.Path, "cache");
        using var client = new HttpClient(transport) { BaseAddress = new Uri("https://example.test") };
        using var fetcher = new ClaudeOAuthUsageFetcher(temp.Path, allowDelegatedRefresh: false, useDiskCache: false,
            httpClient: client, delegatedRefresh: _ => throw new InvalidOperationException("An owner client must never launch."), cacheDirectory: cache);
        var result = await fetcher.FetchAsync(CancellationToken.None);
        Assert.Equal(0d, result!.FiveHourUsedPercent);
        Assert.Equal(1, transport.RequestCount);
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void Mapper_UnknownIsNull_ConfirmedZeroRetained_AndFinitePercentClamped()
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new UsagePulse("codex", now, 0, 100, 200, 100, null, null, null, null,
            [new NamedQuotaBucket("unknown", float.NaN), new NamedQuotaBucket("zero", 0f)]);
        var mapped = StateMapper.Map("codex", new(usage, null, "Updated", now, ReadingConfidence.High, ReadingSource.Api));
        Assert.Equal(0d, mapped.SessionProgress);
        Assert.Equal(100d, mapped.WeeklyProgress);
        Assert.Null(mapped.NamedBuckets[0].Percentage);
        Assert.Equal(0d, mapped.NamedBuckets[1].Percentage);
        Assert.Null(StateMapper.Percentage(5, null));
        Assert.Null(StateMapper.Clamp(double.PositiveInfinity));
    }

    [Fact]
    public void Mapper_SavedAntigravityQuotaDoesNotClaimInteractiveClientLogin()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in new[] { "antigravity", "antigravity-cli" })
        {
            var usage = new UsagePulse(id, now, null, null, null, null, null, null, null, null,
                [new NamedQuotaBucket("weekly", "Weekly", 0)], "synthetic@example.test", "synthetic-subject", IsSharedAccountQuota: true);
            var reading = new ProviderReading(usage, new(id, id, "synthetic@example.test", null, "Pro", AntigravityRemoteSource.SavedLoginMethod),
                "Live (background quota)", now, ReadingConfidence.High, ReadingSource.Api);
            var mapped = StateMapper.Map(id, reading);
            Assert.Equal("Saved sign-in", mapped.SourceLabel);
            Assert.Equal("ok", mapped.Status);
            Assert.False(mapped.IsSharedAccountQuota); // One unified Antigravity entry has no counterpart badge.
            Assert.Null(mapped.SharedNotice);

            var failed = StateMapper.Map(id, reading with { StatusSummary = new AntigravitySessionException(AntigravityFailure.SignInRequired).Message });
            Assert.False(failed.IsSharedAccountQuota);
            Assert.Null(failed.SharedNotice);
        }
    }

    [Fact]
    public void Mapper_WhitelistExcludesRawStatusAndLoginSecretSentinels_AndEstimatesNoReset()
    {
        const string sentinel = "ACCESS_TOKEN_SENTINEL_12345";
        var now = DateTimeOffset.UtcNow;
        var usage = new UsagePulse("codex", now, 1, null, null, null, null, null, new(TimeSpan.FromHours(5), now.AddHours(3)), null);
        var reading = new ProviderReading(usage, new("codex", sentinel, "alpha@example.test", null, "Plus", sentinel), sentinel, now, ReadingConfidence.High, ReadingSource.LocalLog);
        var mapped = StateMapper.Map("codex", reading);
        var text = WireJson.Serialize(mapped);
        Assert.DoesNotContain(sentinel, text);
        Assert.Contains("alpha@example.test", text);
        Assert.Null(mapped.SessionResetAt);
    }

    [Fact]
    public void Mapper_WeekTotalsUseActualDailyBreakdown()
    {
        // Wednesday must include Monday but exclude the preceding Sunday, despite it being only three days ago.
        var computedAt = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).ToLocalTime();
        var today = DateOnly.FromDateTime(computedAt.LocalDateTime);
        TokenLedger Tokens(int n) => new() { StandardInput = n, CachedInput = 0, GeneratedOutput = 0 };
        var digest = new ConsumptionDigest
        {
            TodayTokens = Tokens(3), TodayCostUsd = 3, RollingWindowTokens = Tokens(100), RollingWindowCostUsd = 100,
            RollingWindowDays = 30, ComputedAt = computedAt,
            DailyBreakdown = [new() { Period = today, ModelIdentifier = "a", Tokens = Tokens(3), ComputedCostUsd = 3 },
                new() { Period = today.AddDays(-2), ModelIdentifier = "b", Tokens = Tokens(7), ComputedCostUsd = 7 },
                new() { Period = today.AddDays(-3), ModelIdentifier = "c", Tokens = Tokens(90), ComputedCostUsd = 90 }]
        };
        var usage = new UsagePulse("codex", DateTimeOffset.Now, 0, 100, 0, 100, null, digest, null, null);
        var mapped = StateMapper.Map("codex", new(usage, null, "Updated", DateTimeOffset.Now, ReadingConfidence.High, ReadingSource.Api));
        Assert.Equal(10, mapped.Consumption!.WeekTokens);
        Assert.Equal(10, mapped.Consumption.WeekCostUsd);
        Assert.Equal(100, mapped.Consumption.MonthTokens);
    }

    [Theory]
    [InlineData("{\"enabled\":{\"claude:work\":false}}")]
    [InlineData("{\"enabled\":{\"codex\":1}}")]
    [InlineData("{\"enabled\":false}")]
    [InlineData("{\"refreshMinutes\":0}")]
    [InlineData("{\"refreshMinutes\":61}")]
    [InlineData("{\"refreshMinutes\":1.5}")]
    [InlineData("{\"paths\":{\"codexAuthPath\":null}}")]
    [InlineData("{\"paths\":{\"unexpected\":\"x\"}}")]
    [InlineData("{\"paths\":{\"codexAuthPath\":\"bad\\u0000path\"}}")]
    [InlineData("{\"startAtLogin\":true}")]
    [InlineData("{\"enabled\":{\"codex\":true,\"codex\":false}}")]
    public void InvalidSettingsAreRejectedWithoutPartialMutation(string text)
    {
        var settings = new AppSettings();
        var before = WireJson.Serialize(SettingsValidation.Snapshot(settings));
        Assert.Throws<InvalidSettingsException>(() => SettingsValidation.ValidatePatch(Json(text), SettingsValidation.Snapshot(settings)));
        Assert.Equal(before, WireJson.Serialize(SettingsValidation.Snapshot(settings)));
    }

    [Fact]
    public async Task Settings_PersistAtomicallyAndReload_OnlyIsolatedModernFile()
    {
        using var temp = new TemporaryDirectory();
        var store = new ModernSettingsStore(Path.Combine(temp.Path, "modern", "settings.json"));
        var settings = await store.LoadAsync();
        var first = SettingsValidation.ValidatePatch(Json("{\"enabled\":{\"claude\":false},\"refreshMinutes\":2}"), SettingsValidation.Snapshot(settings));
        var second = SettingsValidation.ValidatePatch(Json("{\"refreshMinutes\":3,\"paths\":{\"codexLogDir\":\"fixture-path\"}}"), first);
        await Task.WhenAll(store.SaveAsync(first), store.SaveAsync(second));
        var loaded = await store.LoadAsync();
        Assert.False(loaded.ClaudeEnabled);
        Assert.Equal(3, loaded.RefreshMinutes);
        Assert.Equal("fixture-path", loaded.CodexLogDir);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!));
    }

    [Fact]
    public async Task Runtime_DistinctAccounts_SharedQuota_ClaudeWorstFirst_ImmediateFiltering()
    {
        using var temp = new TemporaryDirectory();
        var store = new ModernSettingsStore(Path.Combine(temp.Path, "settings.json"));
        await using var runtime = new BridgeRuntime(new AppSettings(), store, true, SourceFactory.CreateFixtures());
        await runtime.StartAsync();
        await Eventually(() => runtime.GetState().Providers.Length == 5 && !runtime.GetState().IsRefreshing);
        var state = runtime.GetState();
        Assert.NotEqual(state.Providers[0].AccountId, state.Providers[1].AccountId);
        Assert.False(state.Providers[0].IsSharedAccountQuota);
        Assert.False(state.Providers.Single(p => p.Id == "antigravity").IsSharedAccountQuota);
        Assert.Single(state.Providers, p => p.Family == "antigravity");
        Assert.Equal("Antigravity", state.Providers[2].DisplayName);
        Assert.Equal("claude:work", state.Providers[3].Id);
        await runtime.UpdateSettingsAsync(Json("{\"enabled\":{\"antigravity\":false,\"claude\":false}}"));
        var filtered = runtime.GetState();
        Assert.DoesNotContain(filtered.Providers, p => p.Family == "antigravity" || p.Id.StartsWith("claude:"));
        Assert.Equal(2, filtered.Providers.Length);
    }

    [Fact]
    public async Task Protocol_HandshakeMalformedFramesAndConcurrentSettingsRemainResponsive()
    {
        using var temp = new TemporaryDirectory();
        var store = new ModernSettingsStore(Path.Combine(temp.Path, "settings.json"));
        await using var runtime = new BridgeRuntime(new AppSettings(), store, true, SourceFactory.CreateFixtures(1200));
        var output = new RecordingWriter();
        await using var writer = new ProtocolWriter(output);
        using var input = new PushReader();
        var task = new ProtocolServer(runtime, writer).RunAsync(input);
        await output.WaitFor(v => v.TryGetProperty("method", out var m) && m.GetString() == "ready");
        input.Push("bad json\n");
        await output.WaitFor(v => v.TryGetProperty("error", out var error) && error.GetProperty("code").GetInt32() == -32700);
        var timer = Stopwatch.StartNew();
        input.Push("{\"jsonrpc\":\"2.0\",\"id\":\"r\",\"method\":\"refresh\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"u\",\"method\":\"updateSettings\",\"params\":{\"enabled\":{\"codex\":false}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":\"s\",\"method\":\"getSettings\"}\n");
        var settings = await output.WaitFor(v => v.TryGetProperty("id", out var id) && id.GetString() == "s");
        Assert.False(settings.GetProperty("result").GetProperty("enabled").GetProperty("codex").GetBoolean());
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(900), "Commands must not wait for the 1200ms source read.");
        await output.WaitFor(v => v.TryGetProperty("id", out var id) && id.GetString() == "u");
        input.Push("{\"jsonrpc\":\"2.0\",\"id\":\"q\",\"method\":\"shutdown\"}\n");
        await task.WaitAsync(TimeSpan.FromSeconds(3));
        await output.WaitFor(v => v.TryGetProperty("id", out var id) && id.GetString() == "q");
    }

    [Fact]
    public async Task Protocol_OverlongAndInvalidEnvelopeRecover_UnknownPatchNeverEchoed()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = new BridgeRuntime(new AppSettings(), new ModernSettingsStore(Path.Combine(temp.Path, "settings.json")), true, SourceFactory.CreateFixtures());
        var output = new StringWriter();
        await using (var writer = new ProtocolWriter(output))
        {
            var invalid = "{\"jsonrpc\":\"2.0\",\"id\":\"bad\",\"method\":\"updateSettings\",\"params\":{\"access_token\":\"SECRET_SENTINEL\"}}\n";
            var requests = new string('x', ProtocolServer.MaximumFrameCharacters + 10) + "\n[]\n" + invalid +
                "{\"jsonrpc\":\"2.0\",\"id\":\"done\",\"method\":\"shutdown\"}\n";
            await new ProtocolServer(runtime, writer).RunAsync(new StringReader(requests));
        }
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => Json(l)).ToArray();
        Assert.DoesNotContain("SECRET_SENTINEL", output.ToString());
        Assert.Contains(lines, v => v.TryGetProperty("error", out var e) && e.GetProperty("code").GetInt32() == -32602);
        Assert.Contains(lines, v => v.TryGetProperty("id", out var id) && id.GetString() == "done");
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(condition());
    }
}

internal sealed class SyntheticClaudeTransport : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"five_hour\":{\"utilization\":0},\"seven_day\":{\"utilization\":30}}")
        });
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "costats-bridge-tests", Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}

internal sealed class RecordingWriter : StringWriter
{
    private readonly Channel<JsonElement> _frames = Channel.CreateUnbounded<JsonElement>();
    private readonly List<JsonElement> _pending = [];
    public override Task WriteLineAsync(string? value)
    {
        if (value is not null) _frames.Writer.TryWrite(JsonDocument.Parse(value).RootElement.Clone());
        return Task.CompletedTask;
    }
    public async Task<JsonElement> WaitFor(Func<JsonElement, bool> predicate)
    {
        var index = _pending.FindIndex(value => predicate(value));
        if (index >= 0)
        {
            var match = _pending[index];
            _pending.RemoveAt(index);
            return match;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (true)
        {
            var value = await _frames.Reader.ReadAsync(timeout.Token);
            if (predicate(value)) return value;
            // Concurrent protocol responses can arrive in either order. Keep frames for later assertions.
            _pending.Add(value);
        }
    }
}

internal sealed class PushReader : TextReader
{
    private readonly Channel<char> _chars = Channel.CreateUnbounded<char>();
    public void Push(string text) { foreach (var ch in text) _chars.Writer.TryWrite(ch); }
    public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
    {
        var first = await _chars.Reader.ReadAsync(cancellationToken);
        buffer.Span[0] = first;
        var count = 1;
        while (count < buffer.Length && _chars.Reader.TryRead(out var ch)) buffer.Span[count++] = ch;
        return count;
    }
}
