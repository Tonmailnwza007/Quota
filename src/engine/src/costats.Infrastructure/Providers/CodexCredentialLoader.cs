using System.Text.Json;

namespace costats.Infrastructure.Providers;

public static class CodexCredentialLoader
{
    public static CodexAccountRecord? LoadFromContent(string json, string? sourceLabel = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Case 1: Zeron wrapper with "credentials" property
            if (root.TryGetProperty("credentials", out var credentialsElement) &&
                credentialsElement.ValueKind == JsonValueKind.Object)
            {
                string? profileEmail = null;
                string? profilePlan = null;

                if (root.TryGetProperty("profile", out var profileElement) &&
                    profileElement.ValueKind == JsonValueKind.Object)
                {
                    if (profileElement.TryGetProperty("email", out var emailProp) && emailProp.ValueKind == JsonValueKind.String)
                    {
                        profileEmail = emailProp.GetString();
                    }

                    if (profileElement.TryGetProperty("plan", out var planProp) && planProp.ValueKind == JsonValueKind.String)
                    {
                        profilePlan = planProp.GetString();
                    }
                }

                string? accessToken = null;
                string? refreshToken = null;
                string? accountId = null;
                string? idToken = null;

                if (credentialsElement.TryGetProperty("tokens", out var tokensElement) &&
                    tokensElement.ValueKind == JsonValueKind.Object)
                {
                    accessToken = GetStringProperty(tokensElement, "access_token");
                    refreshToken = GetStringProperty(tokensElement, "refresh_token");
                    accountId = GetStringProperty(tokensElement, "account_id");
                    idToken = GetStringProperty(tokensElement, "id_token");
                }
                else
                {
                    accessToken = GetStringProperty(credentialsElement, "access_token");
                    refreshToken = GetStringProperty(credentialsElement, "refresh_token");
                    accountId = GetStringProperty(credentialsElement, "account_id");
                    idToken = GetStringProperty(credentialsElement, "id_token");
                }

                if (string.IsNullOrWhiteSpace(accessToken) &&
                    credentialsElement.TryGetProperty("OPENAI_API_KEY", out var keyProp) && keyProp.ValueKind == JsonValueKind.String)
                {
                    accessToken = keyProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(idToken))
                {
                    return null;
                }

                var (jwtEmail, jwtPlan, jwtAccountId) = ParseIdTokenClaims(idToken);
                // Prefer token claims for identity over potentially stale Zeron profile metadata
                var email = !string.IsNullOrWhiteSpace(jwtEmail) ? jwtEmail : profileEmail;
                var plan = !string.IsNullOrWhiteSpace(jwtPlan) ? jwtPlan : profilePlan;
                var finalAccountId = !string.IsNullOrWhiteSpace(accountId) ? accountId : jwtAccountId;

                return new CodexAccountRecord(
                    AccessToken: accessToken,
                    RefreshToken: refreshToken,
                    AccountId: finalAccountId,
                    IdToken: idToken,
                    Email: email,
                    PlanType: plan,
                    SourceLabel: sourceLabel ?? "Zeron");
            }

            // Case 2: Standard auth.json (tokens object at root)
            if (root.TryGetProperty("tokens", out var standardTokens) &&
                standardTokens.ValueKind == JsonValueKind.Object)
            {
                var accessToken = GetStringProperty(standardTokens, "access_token");
                var refreshToken = GetStringProperty(standardTokens, "refresh_token");
                var accountId = GetStringProperty(standardTokens, "account_id");
                var idToken = GetStringProperty(standardTokens, "id_token");

                if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(idToken))
                {
                    return null;
                }

                var (jwtEmail, jwtPlan, jwtAccountId) = ParseIdTokenClaims(idToken);
                var finalAccountId = !string.IsNullOrWhiteSpace(accountId) ? accountId : jwtAccountId;

                return new CodexAccountRecord(
                    AccessToken: accessToken,
                    RefreshToken: refreshToken,
                    AccountId: finalAccountId,
                    IdToken: idToken,
                    Email: jwtEmail,
                    PlanType: jwtPlan,
                    SourceLabel: sourceLabel ?? "auth.json");
            }

            // Case 3: Flat root OPENAI_API_KEY
            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKeyProp) && apiKeyProp.ValueKind == JsonValueKind.String)
            {
                var key = apiKeyProp.GetString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return new CodexAccountRecord(
                        AccessToken: key,
                        RefreshToken: null,
                        AccountId: null,
                        IdToken: null,
                        Email: null,
                        PlanType: null,
                        SourceLabel: sourceLabel ?? "API Key");
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CodexAccountRecord? LoadFromFile(string filePath, string? sourceLabel = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(filePath);
            return LoadFromContent(content, sourceLabel ?? Path.GetFileName(filePath));
        }
        catch
        {
            return null;
        }
    }

    public static string? DefaultDesktopHomeOverride { get; set; }
    public static string? DefaultZeronAccountsDirOverride { get; set; }

    /// <summary>
    /// Returns the default desktop ~/.codex directory path.
    /// Does not follow CODEX_HOME so custom CLI environments do not divert desktop authentication.
    /// </summary>
    public static string GetDefaultDesktopHome()
    {
        return DefaultDesktopHomeOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    /// <summary>
    /// Returns the default desktop auth path ~/.codex/auth.json.
    /// Does not follow CODEX_HOME so custom CLI environments do not divert desktop authentication.
    /// </summary>
    public static string GetDefaultDesktopAuthPath()
    {
        return Path.Combine(GetDefaultDesktopHome(), "auth.json");
    }

    /// <summary>
    /// Returns the default desktop sessions directory ~/.codex/sessions.
    /// </summary>
    public static string GetDefaultDesktopSessionsDir()
    {
        return Path.Combine(GetDefaultDesktopHome(), "sessions");
    }

    public static string GetZeronAccountsDirectory()
    {
        return DefaultZeronAccountsDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zeron", "agent-accounts", "codex");
    }

    public static CodexAccountRecord? LoadZeronAccount(string accountIdOrFileName, string? accountsDir = null)
    {
        if (string.IsNullOrWhiteSpace(accountIdOrFileName))
        {
            return null;
        }

        var dir = accountsDir ?? GetZeronAccountsDirectory();
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var fileName = accountIdOrFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? accountIdOrFileName
            : accountIdOrFileName + ".json";

        var directPath = Path.Combine(dir, fileName);
        if (File.Exists(directPath))
        {
            return LoadFromFile(directPath, $"Zeron ({Path.GetFileNameWithoutExtension(directPath)})");
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var record = LoadFromFile(file, $"Zeron ({Path.GetFileNameWithoutExtension(file)})");
                if (record is null)
                {
                    continue;
                }

                if (string.Equals(record.AccountId, accountIdOrFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return record;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static CodexAccountRecord? AutoDiscoverDistinctZeronAccount(string? excludeAccountId = null, string? accountsDir = null)
    {
        var dir = accountsDir ?? GetZeronAccountsDirectory();
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var candidates = new List<CodexAccountRecord>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var record = LoadFromFile(file, Path.GetFileNameWithoutExtension(file));
                if (record is null || string.IsNullOrWhiteSpace(record.AccountId))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(excludeAccountId) &&
                    string.Equals(record.AccountId, excludeAccountId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(record);
            }
        }
        catch
        {
            return null;
        }

        // Deduplicate records by AccountId so multiple files for the same account do not trigger ambiguity
        var distinctByAccount = candidates
            .GroupBy(c => c.AccountId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only return if there is exactly ONE distinct account ID; never guess when multiple distinct accounts exist!
        if (distinctByAccount.Count == 1)
        {
            var single = distinctByAccount[0].First();
            return single with { SourceLabel = $"Zeron ({single.SourceLabel})" };
        }

        return null;
    }

    public static (string? Email, string? PlanType, string? AccountId) ParseIdTokenClaims(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return (null, null, null);
        }

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2)
            {
                return (null, null, null);
            }

            var payloadBase64 = parts[1];
            var remainder = payloadBase64.Length % 4;
            if (remainder != 0)
            {
                payloadBase64 += new string('=', 4 - remainder);
            }

            payloadBase64 = payloadBase64.Replace('-', '+').Replace('_', '/');
            var jsonBytes = Convert.FromBase64String(payloadBase64);
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            string? email = null;
            if (root.TryGetProperty("email", out var emailProp) && emailProp.ValueKind == JsonValueKind.String)
            {
                email = emailProp.GetString();
            }

            string? planType = null;
            string? accountId = null;

            if (root.TryGetProperty("https://api.openai.com/auth", out var authObj) &&
                authObj.ValueKind == JsonValueKind.Object)
            {
                planType = GetStringProperty(authObj, "chatgpt_plan_type") ?? GetStringProperty(authObj, "plan_type");
                accountId = GetStringProperty(authObj, "chatgpt_account_id") ?? GetStringProperty(authObj, "account_id");
            }

            planType ??= GetStringProperty(root, "chatgpt_plan_type") ?? GetStringProperty(root, "plan_type");
            accountId ??= GetStringProperty(root, "chatgpt_account_id") ?? GetStringProperty(root, "account_id");

            return (email, planType, accountId);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }
}
