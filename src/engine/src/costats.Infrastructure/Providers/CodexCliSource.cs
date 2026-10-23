using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Usage;

namespace costats.Infrastructure.Providers;

public sealed class CodexCliSource : ISignalSource
{
    private readonly UsageLogScanner _scanner;
    private readonly CodexOAuthUsageFetcher _oauthFetcher;
    private readonly ExpenseAnalyzer _expenseAnalyzer;
    private readonly AppSettings? _settings;

    public ProviderProfile Profile => ProviderCatalog.CodexCli;

    public CodexCliSource(
        AppSettings? settings = null,
        CodexOAuthUsageFetcher? oauthFetcher = null,
        UsageLogScanner? scanner = null,
        ExpenseAnalyzer? expenseAnalyzer = null)
    {
        _settings = settings;
        _oauthFetcher = oauthFetcher ?? new CodexOAuthUsageFetcher();
        _scanner = scanner ?? new UsageLogScanner();
        _expenseAnalyzer = expenseAnalyzer ?? new ExpenseAnalyzer();
    }

    public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // CLI credential resolution:
        // 1. Explicit CodexCliAuthPath if configured (NO fallback if invalid)
        // 2. Explicit CodexCliZeronAccountId if configured (NO fallback if invalid)
        // 3. Explicit CODEX_HOME (or Settings.CodexCliHome):
        //    - If equal to desktop home: use it and mark duplicate/shared account ("Shared (~/.codex)")
        //    - If custom: look for auth.json in that home. Missing/invalid must report that CLI source error with strictly NO auto-Zeron fallback.
        // 4. Strict unique distinct Zeron discovery (excluding the actual selected desktop account ID,
        //    including when desktop used CodexDesktopZeronAccountId).
        CodexAccountRecord? creds = null;
        string? unconfiguredError = null;

        var codexHome = !string.IsNullOrWhiteSpace(_settings?.CodexCliHome)
            ? _settings.CodexCliHome.Trim()
            : Environment.GetEnvironmentVariable("CODEX_HOME");

        if (!string.IsNullOrWhiteSpace(_settings?.CodexCliAuthPath))
        {
            creds = CodexCredentialLoader.LoadFromFile(_settings.CodexCliAuthPath, "CLI Config");
            if (creds is null)
            {
                unconfiguredError = "Configured CLI auth file not found";
            }
        }
        else if (!string.IsNullOrWhiteSpace(_settings?.CodexCliZeronAccountId))
        {
            creds = CodexCredentialLoader.LoadZeronAccount(_settings.CodexCliZeronAccountId);
            if (creds is null)
            {
                unconfiguredError = $"Configured CLI Zeron account '{_settings.CodexCliZeronAccountId}' not found";
            }
        }
        else if (!string.IsNullOrWhiteSpace(codexHome))
        {
            var home = codexHome.Trim();
            var desktopHome = CodexCredentialLoader.GetDefaultDesktopHome();
            bool isSameAsDesktop = false;
            try
            {
                isSameAsDesktop = string.Equals(Path.GetFullPath(home), Path.GetFullPath(desktopHome), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Invalid path format
            }

            if (isSameAsDesktop)
            {
                var authPath = Path.Combine(home, "auth.json");
                creds = CodexCredentialLoader.LoadFromFile(authPath, "Shared (~/.codex)");
                if (creds is null)
                {
                    unconfiguredError = "Shared desktop auth file not found";
                }
            }
            else
            {
                var authPath = Path.Combine(home, "auth.json");
                creds = CodexCredentialLoader.LoadFromFile(authPath, "CLI ($CODEX_HOME)");
                if (creds is null)
                {
                    unconfiguredError = "Configured CODEX_HOME auth.json not found";
                }
            }
        }
        else
        {
            // Auto-discovery mode:
            // First resolve the actual selected desktop identity to exclude
            CodexAccountRecord? desktopCreds = null;
            if (!string.IsNullOrWhiteSpace(_settings?.CodexAuthPath))
            {
                desktopCreds = CodexCredentialLoader.LoadFromFile(_settings.CodexAuthPath);
            }
            else if (!string.IsNullOrWhiteSpace(_settings?.CodexDesktopZeronAccountId))
            {
                desktopCreds = CodexCredentialLoader.LoadZeronAccount(_settings.CodexDesktopZeronAccountId);
            }
            else
            {
                desktopCreds = CodexCredentialLoader.LoadFromFile(CodexCredentialLoader.GetDefaultDesktopAuthPath());
            }

            creds = CodexCredentialLoader.AutoDiscoverDistinctZeronAccount(desktopCreds?.AccountId);
        }

        // CLI log root resolution:
        // 1. Explicit CodexCliLogDir if configured (authoritative)
        // 2. If explicit CodexCliAuthPath was configured: <authDir>/sessions
        // 3. If explicit CodexCliZeronAccountId was configured: default shared ~/.codex/sessions (not redirected by global CODEX_HOME)
        // 4. If CODEX_HOME was used for credentials: CODEX_HOME/sessions
        // 5. Otherwise (Auto-Zeron / default): default shared ~/.codex/sessions
        string? cliLogDir = null;
        if (!string.IsNullOrWhiteSpace(_settings?.CodexCliLogDir))
        {
            cliLogDir = _settings.CodexCliLogDir;
        }
        else if (!string.IsNullOrWhiteSpace(_settings?.CodexCliAuthPath))
        {
            try
            {
                var authDir = Path.GetDirectoryName(Path.GetFullPath(_settings.CodexCliAuthPath));
                cliLogDir = !string.IsNullOrWhiteSpace(authDir) ? Path.Combine(authDir, "sessions") : null;
            }
            catch
            {
                cliLogDir = null;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_settings?.CodexCliZeronAccountId))
        {
            cliLogDir = CodexCredentialLoader.GetDefaultDesktopSessionsDir();
        }
        else if (!string.IsNullOrWhiteSpace(codexHome))
        {
            cliLogDir = Path.Combine(codexHome.Trim(), "sessions");
        }
        else
        {
            cliLogDir = CodexCredentialLoader.GetDefaultDesktopSessionsDir();
        }

        return CodexSourceCommon.ReadAsync(
            profile: Profile,
            creds: creds,
            explicitLogDir: cliLogDir,
            oauthFetcher: _oauthFetcher,
            scanner: _scanner,
            expenseAnalyzer: _expenseAnalyzer,
            defaultSource: ReadingSource.Cli,
            cancellationToken: cancellationToken,
            unconfiguredStatusSummary: unconfiguredError);
    }
}
