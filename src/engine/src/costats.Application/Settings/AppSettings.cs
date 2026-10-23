namespace costats.Application.Settings;

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public string Hotkey { get; set; } = "Ctrl+Alt+U";
    public bool StartAtLogin { get; set; } = false;

    // Provider enablement toggles (6 distinct providers)
    public bool CodexEnabled { get; set; } = true;
    public bool CodexCliEnabled { get; set; } = true;
    public bool AntigravityEnabled { get; set; } = true;
    public bool AntigravityCliEnabled { get; set; } = true;
    public bool ClaudeEnabled { get; set; } = true;
    public bool CopilotEnabled { get; set; } = false;

    // Quota-only display/alert preferences; original application defaults remain unchanged.
    public bool ShowUsageDetails { get; set; } = false;
    public bool QuotaNotificationsEnabled { get; set; } = true;
    public int QuotaWarningPercent { get; set; } = 10;
    public int QuotaCriticalPercent { get; set; } = 5;

    // Custom paths and overrides for independent accounts
    public string? CodexAuthPath { get; set; }
    public string? CodexLogDir { get; set; }
    public string? CodexCliHome { get; set; }
    public string? CodexCliAuthPath { get; set; }
    public string? CodexCliLogDir { get; set; }
    public string? AntigravityAppPath { get; set; }
    public string? AntigravityCliPath { get; set; }
    public string? AntigravityCliCredentialTarget { get; set; }
    public string? AntigravityCliAuthPath { get; set; }

    // Modern unified quota source. Legacy App/CLI members remain for the original engine consumers.
    public string AntigravitySource { get; set; } = "saved";
    public string? AntigravityAuthPath { get; set; }
    public string? AntigravityCredentialTarget { get; set; }

    // Safe account selection (e.g. specific Zeron account record filename or id, e.g. "account_sample_id")
    public string? CodexCliZeronAccountId { get; set; }
    public string? CodexDesktopZeronAccountId { get; set; }

    /// <summary>
    /// Whether multicc integration is enabled. Default true when multicc is detected.
    /// </summary>
    public bool MulticcEnabled { get; set; } = true;

    /// <summary>
    /// When set, only show this single profile instead of all profiles stacked.
    /// Null means "show all profiles" (stacked mode).
    /// </summary>
    public string? MulticcSelectedProfile { get; set; }

    /// <summary>
    /// Override path for multicc config directory. Null means auto-detect (~/.multicc or $MULTICC_DIR).
    /// </summary>
    public string? MulticcConfigPath { get; set; }

    public bool IsProviderEnabled(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return true;

        var normalized = providerId.Trim();
        if (normalized.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
        {
            if (!ClaudeEnabled)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(MulticcSelectedProfile))
            {
                var profile = normalized["claude:".Length..].Trim();
                return string.Equals(profile, MulticcSelectedProfile.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        if (string.Equals(normalized, "codex", StringComparison.OrdinalIgnoreCase))
            return CodexEnabled;
        if (string.Equals(normalized, "codex-cli", StringComparison.OrdinalIgnoreCase))
            return CodexCliEnabled;
        if (string.Equals(normalized, "antigravity", StringComparison.OrdinalIgnoreCase))
            return AntigravityEnabled;
        if (string.Equals(normalized, "antigravity-cli", StringComparison.OrdinalIgnoreCase))
            return AntigravityCliEnabled;
        if (string.Equals(normalized, "claude", StringComparison.OrdinalIgnoreCase))
            return ClaudeEnabled;
        if (string.Equals(normalized, "copilot", StringComparison.OrdinalIgnoreCase))
            return CopilotEnabled;

        return true;
    }
}
