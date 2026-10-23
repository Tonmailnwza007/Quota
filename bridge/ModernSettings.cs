using System.Text.Json;
using costats.Application.Settings;

namespace Costats.Bridge;

public sealed record NotificationSettingsDto(bool Enabled = true, int WarningPercent = 10, int CriticalPercent = 5);
public sealed record SettingsDto(Dictionary<string, bool> Enabled, int RefreshMinutes, Dictionary<string, string> Paths,
    bool ShowUsageDetails = false, NotificationSettingsDto? Notifications = null, string AntigravitySource = "saved")
{
    public NotificationSettingsDto Notifications { get; init; } = Notifications ?? new();
}

public sealed class InvalidSettingsException : Exception;

public static class SettingsValidation
{
    public static readonly string[] Providers = ["codex", "codex-cli", "antigravity", "claude", "copilot"];
    public static readonly string[] Paths = ["codexAuthPath", "codexLogDir", "codexCliHome", "codexCliAuthPath", "codexCliLogDir", "codexDesktopZeronAccountId", "codexCliZeronAccountId", "antigravityAuthPath", "antigravityCredentialTarget"];
    private static readonly string[] LegacyPaths = ["antigravityAppPath", "antigravityCliAuthPath", "antigravityCliCredentialTarget"];

    public static SettingsDto Snapshot(AppSettings s) => new(new(StringComparer.Ordinal)
    {
        ["codex"] = s.CodexEnabled, ["codex-cli"] = s.CodexCliEnabled, ["antigravity"] = s.AntigravityEnabled,
        ["claude"] = s.ClaudeEnabled, ["copilot"] = s.CopilotEnabled
    }, s.RefreshMinutes, new(StringComparer.Ordinal)
    {
        ["codexAuthPath"] = s.CodexAuthPath ?? "", ["codexLogDir"] = s.CodexLogDir ?? "", ["codexCliHome"] = s.CodexCliHome ?? "",
        ["codexCliAuthPath"] = s.CodexCliAuthPath ?? "", ["codexCliLogDir"] = s.CodexCliLogDir ?? "",
        ["codexDesktopZeronAccountId"] = s.CodexDesktopZeronAccountId ?? "", ["codexCliZeronAccountId"] = s.CodexCliZeronAccountId ?? "",
        ["antigravityAuthPath"] = s.AntigravityAuthPath ?? "", ["antigravityCredentialTarget"] = s.AntigravityCredentialTarget ?? ""
    }, s.ShowUsageDetails, new(s.QuotaNotificationsEnabled, s.QuotaWarningPercent, s.QuotaCriticalPercent), s.AntigravitySource);

    public static SettingsDto ValidatePatch(JsonElement patch, SettingsDto current) => Validate(patch, current, allowChoose: false);

    private static SettingsDto Validate(JsonElement patch, SettingsDto current, bool allowChoose)
    {
        RequireObject(patch);
        var enabled = new Dictionary<string, bool>(current.Enabled, StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(current.Paths, StringComparer.Ordinal);
        var minutes = current.RefreshMinutes;
        var showDetails = current.ShowUsageDetails;
        var notifications = current.Notifications;
        var source = current.AntigravitySource;
        foreach (var prop in patch.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "enabled":
                    RequireObject(prop.Value);
                    foreach (var flag in prop.Value.EnumerateObject())
                    {
                        if (!Providers.Contains(flag.Name) || flag.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidSettingsException();
                        enabled[flag.Name] = flag.Value.GetBoolean();
                    }
                    break;
                case "paths":
                    RequireObject(prop.Value);
                    foreach (var path in prop.Value.EnumerateObject())
                    {
                        if (!Paths.Contains(path.Name) || path.Value.ValueKind != JsonValueKind.String) throw new InvalidSettingsException();
                        var value = path.Value.GetString()!;
                        if (value.Length > 4096 || value.Any(char.IsControl)) throw new InvalidSettingsException();
                        paths[path.Name] = value.Trim();
                    }
                    break;
                case "refreshMinutes":
                    if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out minutes) || minutes is < 1 or > 60) throw new InvalidSettingsException();
                    break;
                case "antigravitySource":
                    if (prop.Value.ValueKind != JsonValueKind.String) throw new InvalidSettingsException();
                    source = prop.Value.GetString()!;
                    if (source is not ("saved" or "custom") && !(allowChoose && source == "choose")) throw new InvalidSettingsException();
                    break;
                case "showUsageDetails":
                    if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidSettingsException();
                    showDetails = prop.Value.GetBoolean();
                    break;
                case "notifications":
                    RequireObject(prop.Value);
                    var notifyEnabled = notifications.Enabled;
                    var warning = notifications.WarningPercent;
                    var critical = notifications.CriticalPercent;
                    foreach (var field in prop.Value.EnumerateObject())
                    {
                        switch (field.Name)
                        {
                            case "enabled":
                                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidSettingsException();
                                notifyEnabled = field.Value.GetBoolean();
                                break;
                            case "warningPercent":
                                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out warning)) throw new InvalidSettingsException();
                                break;
                            case "criticalPercent":
                                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out critical)) throw new InvalidSettingsException();
                                break;
                            default: throw new InvalidSettingsException();
                        }
                    }
                    if (critical < 0 || warning > 100 || critical >= warning) throw new InvalidSettingsException();
                    notifications = new(notifyEnabled, warning, critical);
                    break;
                default: throw new InvalidSettingsException();
            }
        }
        if (source == "custom" && string.IsNullOrWhiteSpace(paths["antigravityAuthPath"]) && string.IsNullOrWhiteSpace(paths["antigravityCredentialTarget"]))
            throw new InvalidSettingsException();
        return new(enabled, minutes, paths, showDetails, notifications, source);
    }

    /// <summary>Read-only migration. Legacy keys are accepted here only; public updates remain canonical.</summary>
    public static SettingsDto MigrateForLoad(JsonElement document)
    {
        RequireObject(document);
        var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasLegacyCliFlag = false;
        var legacyCliEnabled = true;
        var hasLegacyCustomKeys = false;
        var hasCanonicalPaths = false;
        var hasCanonicalSource = false;
        foreach (var field in document.EnumerateObject())
        {
            switch (field.Name)
            {
                case "enabled":
                    RequireObject(field.Value);
                    foreach (var flag in field.Value.EnumerateObject())
                    {
                        if (flag.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidSettingsException();
                        if (flag.Name == "antigravity-cli") { hasLegacyCliFlag = true; legacyCliEnabled = flag.Value.GetBoolean(); }
                        else if (Providers.Contains(flag.Name)) flags.Add(flag.Name, flag.Value.GetBoolean());
                        else throw new InvalidSettingsException();
                    }
                    break;
                case "paths":
                    RequireObject(field.Value);
                    foreach (var path in field.Value.EnumerateObject())
                    {
                        if (!Paths.Contains(path.Name) && !LegacyPaths.Contains(path.Name) || path.Value.ValueKind != JsonValueKind.String) throw new InvalidSettingsException();
                        var value = path.Value.GetString()!;
                        if (value.Length > 4096 || value.Any(char.IsControl)) throw new InvalidSettingsException();
                        paths.Add(path.Name, value.Trim());
                        hasLegacyCustomKeys |= path.Name is "antigravityCliAuthPath" or "antigravityCliCredentialTarget";
                        hasCanonicalPaths |= path.Name is "antigravityAuthPath" or "antigravityCredentialTarget";
                    }
                    break;
                case "antigravitySource": hasCanonicalSource = true; normalized[field.Name] = field.Value.Clone(); break;
                case "refreshMinutes" or "showUsageDetails" or "notifications": normalized[field.Name] = field.Value.Clone(); break;
                default: throw new InvalidSettingsException();
            }
        }
        var appEnabled = flags.GetValueOrDefault("antigravity", true);
        if (hasLegacyCliFlag) flags["antigravity"] = appEnabled || legacyCliEnabled;
        if (!paths.ContainsKey("antigravityAuthPath") && paths.TryGetValue("antigravityCliAuthPath", out var oldFile)) paths["antigravityAuthPath"] = oldFile;
        if (!paths.ContainsKey("antigravityCredentialTarget") && paths.TryGetValue("antigravityCliCredentialTarget", out var oldTarget)) paths["antigravityCredentialTarget"] = oldTarget;
        foreach (var key in LegacyPaths) paths.Remove(key);
        normalized["enabled"] = flags;
        normalized["paths"] = paths;
        if (!hasCanonicalSource)
        {
            var custom = !string.IsNullOrWhiteSpace(paths.GetValueOrDefault("antigravityAuthPath")) ||
                (!string.IsNullOrWhiteSpace(paths.GetValueOrDefault("antigravityCredentialTarget")) &&
                !string.Equals(paths.GetValueOrDefault("antigravityCredentialTarget"), "gemini:antigravity", StringComparison.OrdinalIgnoreCase));
            var cliEnabled = hasLegacyCliFlag ? legacyCliEnabled : hasLegacyCustomKeys && !flags.ContainsKey("antigravity");
            var source = !custom ? "saved" : appEnabled && !cliEnabled ? "saved" : !appEnabled && cliEnabled ? "custom" : "choose";
            // A canonical custom path without an explicit mode must never silently select the default saved account.
            if (hasCanonicalPaths && custom && !hasLegacyCliFlag && !hasLegacyCustomKeys) source = "choose";
            normalized["antigravitySource"] = source;
        }
        // Existing legacy documents retain old implicit provider defaults. Only missing/new files use modern defaults.
        var defaults = hasCanonicalSource || hasCanonicalPaths ? ModernDefaults() : new AppSettings();
        using var canonical = JsonDocument.Parse(JsonSerializer.Serialize(normalized, WireJson.Options));
        return Validate(canonical.RootElement, Snapshot(defaults), allowChoose: true);
    }

    public static AppSettings ModernDefaults() => new() { ClaudeEnabled = false, CopilotEnabled = false, AntigravityCliEnabled = false };

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidSettingsException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!seen.Add(property.Name)) throw new InvalidSettingsException();
    }

    public static void Apply(SettingsDto dto, AppSettings s)
    {
        s.CodexEnabled = dto.Enabled["codex"]; s.CodexCliEnabled = dto.Enabled["codex-cli"];
        s.AntigravityEnabled = dto.Enabled["antigravity"]; s.AntigravityCliEnabled = false;
        s.ClaudeEnabled = dto.Enabled["claude"]; s.CopilotEnabled = dto.Enabled["copilot"]; s.RefreshMinutes = dto.RefreshMinutes;
        s.ShowUsageDetails = dto.ShowUsageDetails;
        s.QuotaNotificationsEnabled = dto.Notifications.Enabled;
        s.QuotaWarningPercent = dto.Notifications.WarningPercent;
        s.QuotaCriticalPercent = dto.Notifications.CriticalPercent;
        string? Path(string key) => string.IsNullOrWhiteSpace(dto.Paths[key]) ? null : dto.Paths[key];
        s.CodexAuthPath = Path("codexAuthPath"); s.CodexLogDir = Path("codexLogDir"); s.CodexCliHome = Path("codexCliHome");
        s.CodexCliAuthPath = Path("codexCliAuthPath"); s.CodexCliLogDir = Path("codexCliLogDir");
        s.CodexDesktopZeronAccountId = Path("codexDesktopZeronAccountId"); s.CodexCliZeronAccountId = Path("codexCliZeronAccountId");
        s.AntigravitySource = dto.AntigravitySource;
        s.AntigravityAuthPath = Path("antigravityAuthPath"); s.AntigravityCredentialTarget = Path("antigravityCredentialTarget");
    }
}

public sealed class ModernSettingsStore(string path)
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    public string FilePath { get; } = Path.GetFullPath(path);

    public async Task<AppSettings> LoadAsync()
    {
        var settings = SettingsValidation.ModernDefaults();
        if (!File.Exists(FilePath)) return settings;
        try
        {
            if (new FileInfo(FilePath).Length > 65536) throw new InvalidSettingsException();
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
            SettingsValidation.Apply(SettingsValidation.MigrateForLoad(doc.RootElement), settings);
        }
        catch
        {
            // Never modify or back up a corrupt settings file on read. In particular, no legacy file is loaded.
            settings = SettingsValidation.ModernDefaults();
            settings.CodexEnabled = settings.CodexCliEnabled = settings.AntigravityEnabled = settings.ClaudeEnabled = settings.CopilotEnabled = false;
            settings.AntigravitySource = "choose";
            Console.Error.WriteLine("Modern settings could not be loaded; providers are disabled until preferences are repaired.");
        }
        return settings;
    }

    public Task SaveAsync(SettingsDto settings)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, WireJson.Options);
        lock (_gate)
        {
            var previous = _tail;
            return _tail = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch { /* Continue after an earlier disk failure. */ }
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                    File.Move(temporary, FilePath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
        }
    }

    public Task FlushAsync() { lock (_gate) return _tail; }
}
