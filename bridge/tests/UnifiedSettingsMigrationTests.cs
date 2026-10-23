using System.Text.Json;
using costats.Application.Settings;

namespace Costats.Bridge.Tests;

public sealed class UnifiedSettingsMigrationTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task MissingModernFileStartsOnlyCodexPairAndUnifiedAntigravity_WithoutChangingUpstreamDefaults()
    {
        using var temp = new TemporaryDirectory();
        var file = Path.Combine(temp.Path, "new-settings.json");
        var settings = await new ModernSettingsStore(file).LoadAsync();
        var snapshot = SettingsValidation.Snapshot(settings);

        Assert.Equal(new[] { "antigravity", "claude", "codex", "codex-cli", "copilot" }, snapshot.Enabled.Keys.Order().ToArray());
        Assert.True(snapshot.Enabled["codex"]);
        Assert.True(snapshot.Enabled["codex-cli"]);
        Assert.True(snapshot.Enabled["antigravity"]);
        Assert.False(snapshot.Enabled["claude"]);
        Assert.False(snapshot.Enabled["copilot"]);
        Assert.False(settings.AntigravityCliEnabled);
        Assert.Equal("saved", snapshot.AntigravitySource);
        Assert.Equal(9, snapshot.Paths.Count);
        Assert.DoesNotContain("antigravity-cli", snapshot.Enabled.Keys);
        Assert.DoesNotContain("antigravityAppPath", snapshot.Paths.Keys);
        Assert.DoesNotContain("antigravityCliAuthPath", snapshot.Paths.Keys);
        Assert.True(new AppSettings().ClaudeEnabled);
        Assert.True(new AppSettings().AntigravityCliEnabled);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(false, false, false, "saved")]
    [InlineData(false, true, true, "saved")]
    [InlineData(true, false, true, "saved")]
    [InlineData(true, true, true, "saved")]
    public async Task LegacyProviderFlagsAreOrMergedOnlyWhenCliKeyExists(bool app, bool cli, bool unified, string source)
    {
        var document = JsonSerializer.Serialize(new { enabled = new Dictionary<string, bool> { ["antigravity"] = app, ["antigravity-cli"] = cli } });
        var settings = await Load(document);
        Assert.Equal(unified, settings.AntigravityEnabled);
        Assert.False(settings.AntigravityCliEnabled);
        Assert.Equal(source, settings.AntigravitySource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalAntigravityFlagAloneIsKeptAsProvided(bool enabled)
    {
        var document = JsonSerializer.Serialize(new { enabled = new Dictionary<string, bool> { ["antigravity"] = enabled } });
        var settings = await Load(document);
        Assert.Equal(enabled, settings.AntigravityEnabled);
        Assert.False(settings.AntigravityCliEnabled);
    }

    [Theory]
    [InlineData(true, true, "choose")]
    [InlineData(true, false, "saved")]
    [InlineData(false, true, "custom")]
    [InlineData(false, false, "choose")]
    public async Task LegacyCustomSelectionPreservesIntentWithoutChoosingAnotherAccount(bool app, bool cli, string source)
    {
        var document = JsonSerializer.Serialize(new
        {
            enabled = new Dictionary<string, bool> { ["antigravity"] = app, ["antigravity-cli"] = cli },
            paths = new { antigravityCliAuthPath = "synthetic-custom-auth.json", antigravityCliCredentialTarget = "synthetic:custom-target" }
        });
        var settings = await Load(document);
        var snapshot = SettingsValidation.Snapshot(settings);
        Assert.Equal(app || cli, settings.AntigravityEnabled);
        Assert.False(settings.AntigravityCliEnabled);
        Assert.Equal(source, settings.AntigravitySource);
        Assert.Equal("synthetic-custom-auth.json", snapshot.Paths["antigravityAuthPath"]);
        Assert.Equal("synthetic:custom-target", snapshot.Paths["antigravityCredentialTarget"]);
        Assert.DoesNotContain("antigravityCliAuthPath", snapshot.Paths.Keys);
    }

    [Theory]
    [InlineData("{\"paths\":{\"antigravityCliAuthPath\":\"synthetic-custom-auth.json\"}}")]
    [InlineData("{\"paths\":{\"antigravityCliCredentialTarget\":\"synthetic:other-account\"}}")]
    public async Task LegacyCustomSourceWithoutFlagsRequiresExplicitChoice(string document)
    {
        var settings = await Load(document);
        Assert.True(settings.AntigravityEnabled);
        Assert.False(settings.AntigravityCliEnabled);
        Assert.Equal("choose", settings.AntigravitySource);
    }

    [Fact]
    public async Task LegacySharedTargetIsSavedAndDoesNotCreateFalseCustomConflict()
    {
        var settings = await Load("{\"enabled\":{\"antigravity\":true,\"antigravity-cli\":true},\"paths\":{\"antigravityCliCredentialTarget\":\"GEMINI:ANTIGRAVITY\"}}");
        Assert.Equal("saved", settings.AntigravitySource);
        Assert.True(settings.AntigravityEnabled);
    }

    [Theory]
    [InlineData("saved")]
    [InlineData("custom")]
    [InlineData("choose")]
    public async Task CanonicalSourceWinsLegacyInferenceAndRoundTripsIncludingChoose(string source)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var document = JsonSerializer.Serialize(new
        {
            antigravitySource = source,
            enabled = new Dictionary<string, bool> { ["antigravity"] = true, ["antigravity-cli"] = true },
            paths = new { antigravityCliAuthPath = "synthetic-old-account.json", antigravityAuthPath = "synthetic-selected-account.json" }
        });
        await File.WriteAllTextAsync(path, document);
        var store = new ModernSettingsStore(path);
        var first = await store.LoadAsync();
        Assert.Equal(source, first.AntigravitySource);
        Assert.Equal("synthetic-selected-account.json", first.AntigravityAuthPath);
        await store.SaveAsync(SettingsValidation.Snapshot(first));
        var restored = await store.LoadAsync();
        Assert.Equal(source, restored.AntigravitySource);
        Assert.False(restored.AntigravityCliEnabled);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(source, saved.RootElement.GetProperty("antigravitySource").GetString());
        Assert.Equal(5, saved.RootElement.GetProperty("enabled").EnumerateObject().Count());
        Assert.False(saved.RootElement.GetProperty("enabled").TryGetProperty("antigravity-cli", out _));
        Assert.False(saved.RootElement.GetProperty("paths").TryGetProperty("antigravityCliAuthPath", out _));
    }

    [Fact]
    public async Task ExplicitEmptyCanonicalPathsClearOldOverridesRatherThanRestoringThem()
    {
        var settings = await Load("{\"antigravitySource\":\"saved\",\"paths\":{\"antigravityAuthPath\":\"\",\"antigravityCredentialTarget\":\"\",\"antigravityCliAuthPath\":\"synthetic-old-account.json\",\"antigravityCliCredentialTarget\":\"synthetic:old-account\"}}");
        Assert.Equal("saved", settings.AntigravitySource);
        Assert.Null(settings.AntigravityAuthPath);
        Assert.Null(settings.AntigravityCredentialTarget);
    }

    [Fact]
    public async Task CanonicalCustomPathWithoutModeFailsToChooseRatherThanAssumingSavedAccount()
    {
        var settings = await Load("{\"paths\":{\"antigravityAuthPath\":\"synthetic-account.json\"}}");
        Assert.Equal("choose", settings.AntigravitySource);
        Assert.Equal("synthetic-account.json", settings.AntigravityAuthPath);
    }

    [Fact]
    public async Task MigrationPreservesCodexPathsAndOtherPreferencesWithoutWritingFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var codex = SettingsValidation.Paths.Where(key => key.StartsWith("codex", StringComparison.Ordinal)).ToDictionary(key => key, key => "synthetic/" + key);
        var allPaths = new Dictionary<string, string>(codex) { ["antigravityCliAuthPath"] = "synthetic/other-account.json", ["antigravityAppPath"] = "synthetic/app-install" };
        var document = JsonSerializer.Serialize(new
        {
            enabled = new Dictionary<string, bool> { ["codex"] = false, ["codex-cli"] = true, ["antigravity"] = true, ["antigravity-cli"] = true, ["claude"] = true, ["copilot"] = true },
            refreshMinutes = 9, showUsageDetails = true,
            notifications = new { enabled = false, warningPercent = 23, criticalPercent = 7 }, paths = allPaths
        });
        await File.WriteAllTextAsync(path, document);
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);

        var loaded = await new ModernSettingsStore(path).LoadAsync();

        Assert.Equal("choose", loaded.AntigravitySource);
        Assert.Equal(9, loaded.RefreshMinutes);
        Assert.True(loaded.ShowUsageDetails);
        Assert.Equal(new NotificationSettingsDto(false, 23, 7), SettingsValidation.Snapshot(loaded).Notifications);
        Assert.False(loaded.CodexEnabled);
        Assert.True(loaded.CodexCliEnabled);
        Assert.True(loaded.ClaudeEnabled);
        Assert.True(loaded.CopilotEnabled);
        foreach (var (key, value) in codex) Assert.Equal(value, SettingsValidation.Snapshot(loaded).Paths[key]);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"antigravitySource\":\"invalid\"}")]
    [InlineData("{\"antigravitySource\":\"custom\"}")]
    [InlineData("{\"paths\":{\"antigravityCliAuthPath\":false}}")]
    [InlineData("{\"paths\":{\"antigravityAuthPath\":\"\",\"antigravityCliAuthPath\":\"old.json\"},\"antigravitySource\":\"custom\"}")]
    [InlineData("{\"enabled\":{\"antigravity-cli\":\"true\"}}")]
    [InlineData("{\"antigravitySource\":\"saved\",\"antigravitySource\":\"custom\"}")]
    public async Task InvalidLoadFailsClosedWithoutSelectingAnotherAccountOrChangingDisk(string document)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, document);
        var loaded = await new ModernSettingsStore(path).LoadAsync();
        Assert.Equal("choose", loaded.AntigravitySource);
        Assert.All(SettingsValidation.Snapshot(loaded).Enabled.Values, enabled => Assert.False(enabled));
        Assert.False(loaded.AntigravityCliEnabled);
        Assert.Null(loaded.AntigravityAuthPath);
        Assert.Null(loaded.AntigravityCredentialTarget);
        Assert.Equal(document, await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("{\"enabled\":{\"antigravity-cli\":true}}")]
    [InlineData("{\"paths\":{\"antigravityAppPath\":\"old\"}}")]
    [InlineData("{\"paths\":{\"antigravityCliAuthPath\":\"old.json\"}}")]
    [InlineData("{\"paths\":{\"antigravityCliCredentialTarget\":\"old\"}}")]
    [InlineData("{\"antigravitySource\":\"choose\"}")]
    [InlineData("{\"antigravitySource\":null}")]
    [InlineData("{\"antigravitySource\":1}")]
    [InlineData("{\"antigravitySource\":\"other\"}")]
    [InlineData("{\"antigravitySource\":\"custom\"}")]
    [InlineData("{\"unknown\":true}")]
    public void PublicPatchesRejectLegacyUnknownAndChooseWithoutPartialMutation(string patch)
    {
        var current = SettingsValidation.Snapshot(SettingsValidation.ModernDefaults());
        var before = WireJson.Serialize(current);
        Assert.Throws<InvalidSettingsException>(() => SettingsValidation.ValidatePatch(Json(patch), current));
        Assert.Equal(before, WireJson.Serialize(current));
    }

    [Fact]
    public void PublicCustomSelectionAndOtherPreferenceUpdatesPreservePendingChoiceUnlessExplicitlySelected()
    {
        var settings = SettingsValidation.ModernDefaults();
        settings.AntigravitySource = "choose";
        var current = SettingsValidation.Snapshot(settings);
        var unrelated = SettingsValidation.ValidatePatch(Json("{\"refreshMinutes\":2,\"showUsageDetails\":true}"), current);
        Assert.Equal("choose", unrelated.AntigravitySource);
        var custom = SettingsValidation.ValidatePatch(Json("{\"antigravitySource\":\"custom\",\"paths\":{\"antigravityAuthPath\":\"synthetic-auth.json\"}}"), unrelated);
        SettingsValidation.Apply(custom, settings);
        Assert.Equal("custom", settings.AntigravitySource);
        Assert.Equal("synthetic-auth.json", settings.AntigravityAuthPath);
        Assert.False(settings.AntigravityCliEnabled);
        Assert.Equal(2, settings.RefreshMinutes);
        Assert.True(settings.ShowUsageDetails);
        var saved = SettingsValidation.ValidatePatch(Json("{\"antigravitySource\":\"saved\"}"), custom);
        Assert.Equal("saved", saved.AntigravitySource);
        Assert.Equal("synthetic-auth.json", saved.Paths["antigravityAuthPath"]);
    }

    [Fact]
    public void ClearingBothSourcesFromExistingCustomSelectionIsRejectedAtomically()
    {
        var current = SettingsValidation.ValidatePatch(Json("{\"antigravitySource\":\"custom\",\"paths\":{\"antigravityAuthPath\":\"synthetic-account.json\",\"antigravityCredentialTarget\":\"synthetic:custom\"}}"),
            SettingsValidation.Snapshot(SettingsValidation.ModernDefaults()));
        var before = WireJson.Serialize(current);
        Assert.Throws<InvalidSettingsException>(() => SettingsValidation.ValidatePatch(Json("{\"paths\":{\"antigravityAuthPath\":\"\",\"antigravityCredentialTarget\":\"\"}}"), current));
        Assert.Equal(before, WireJson.Serialize(current));
    }

    [Fact]
    public void ProviderAndNotificationChangesRemainPossibleWhileSourceChoiceIsPending()
    {
        var pending = SettingsValidation.ModernDefaults();
        pending.AntigravitySource = "choose";
        var updated = SettingsValidation.ValidatePatch(Json("{\"enabled\":{\"antigravity\":false,\"claude\":true},\"notifications\":{\"warningPercent\":17,\"criticalPercent\":3}}"), SettingsValidation.Snapshot(pending));
        Assert.Equal("choose", updated.AntigravitySource);
        Assert.False(updated.Enabled["antigravity"]);
        Assert.True(updated.Enabled["claude"]);
        Assert.Equal(new NotificationSettingsDto(true, 17, 3), updated.Notifications);
    }

    [Fact]
    public async Task OversizedSettingsFailClosedWithoutChangingOriginalFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var oversized = new string(' ', 65537);
        await File.WriteAllTextAsync(path, oversized);
        var loaded = await new ModernSettingsStore(path).LoadAsync();
        Assert.Equal("choose", loaded.AntigravitySource);
        Assert.All(SettingsValidation.Snapshot(loaded).Enabled.Values, value => Assert.False(value));
        Assert.Equal(oversized, await File.ReadAllTextAsync(path));
    }

    private static async Task<AppSettings> Load(string document)
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, document);
        return await new ModernSettingsStore(path).LoadAsync();
    }
}
