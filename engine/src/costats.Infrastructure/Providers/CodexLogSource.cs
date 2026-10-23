using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Usage;

namespace costats.Infrastructure.Providers;

public sealed class CodexLogSource : ISignalSource
{
    private readonly UsageLogScanner _scanner;
    private readonly CodexOAuthUsageFetcher _oauthFetcher;
    private readonly ExpenseAnalyzer _expenseAnalyzer;
    private readonly AppSettings? _settings;

    public ProviderProfile Profile => ProviderCatalog.Codex;

    public CodexLogSource(
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

        // Desktop credential resolution:
        // 1. Explicit CodexAuthPath if configured (NO fallback if invalid)
        // 2. Explicit CodexDesktopZeronAccountId if configured (NO fallback if invalid)
        // 3. Detected ~/.codex/auth.json (independent of CODEX_HOME)
        CodexAccountRecord? creds = null;
        string? unconfiguredError = null;

        if (!string.IsNullOrWhiteSpace(_settings?.CodexAuthPath))
        {
            creds = CodexCredentialLoader.LoadFromFile(_settings.CodexAuthPath, "Desktop Config");
            if (creds is null)
            {
                unconfiguredError = "Configured desktop auth file not found";
            }
        }
        else if (!string.IsNullOrWhiteSpace(_settings?.CodexDesktopZeronAccountId))
        {
            creds = CodexCredentialLoader.LoadZeronAccount(_settings.CodexDesktopZeronAccountId);
            if (creds is null)
            {
                unconfiguredError = $"Configured desktop Zeron account '{_settings.CodexDesktopZeronAccountId}' not found";
            }
        }
        else
        {
            var defaultPath = CodexCredentialLoader.GetDefaultDesktopAuthPath();
            creds = CodexCredentialLoader.LoadFromFile(defaultPath, "Desktop");
        }

        // Desktop log root: explicit CodexLogDir if configured, otherwise strictly default ~/.codex/sessions
        // Desktop log root never follows CODEX_HOME!
        var desktopLogDir = !string.IsNullOrWhiteSpace(_settings?.CodexLogDir)
            ? _settings.CodexLogDir
            : CodexCredentialLoader.GetDefaultDesktopSessionsDir();

        return CodexSourceCommon.ReadAsync(
            profile: Profile,
            creds: creds,
            explicitLogDir: desktopLogDir,
            oauthFetcher: _oauthFetcher,
            scanner: _scanner,
            expenseAnalyzer: _expenseAnalyzer,
            defaultSource: ReadingSource.LocalLog,
            cancellationToken: cancellationToken,
            unconfiguredStatusSummary: unconfiguredError);
    }
}
