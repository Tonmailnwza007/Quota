using System.Net.Http.Headers;
using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Fetches Codex (OpenAI) usage data via the ChatGPT backend API.
/// This provides accurate utilization percentages directly from OpenAI.
/// </summary>
public sealed class CodexOAuthUsageFetcher : IDisposable
{
    public const string OfficialUsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public CodexOAuthUsageFetcher(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _ownsHttpClient = true;
        }
    }

    public async Task<CodexOAuthUsageResult?> FetchAsync(CodexAccountRecord? credentials, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentials?.AccessToken))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // If the client has a BaseAddress, use relative path, otherwise use absolute endpoint URL
            var requestUri = _httpClient.BaseAddress is not null
                ? new Uri("wham/usage", UriKind.Relative)
                : new Uri(OfficialUsageEndpoint);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.UserAgent.ParseAdd("costats");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            if (!string.IsNullOrWhiteSpace(credentials.AccountId))
            {
                request.Headers.Add("ChatGPT-Account-Id", credentials.AccountId);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseResponse(content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public Task<CodexOAuthUsageResult?> FetchAsync(string authFilePath, CancellationToken cancellationToken = default)
    {
        var credentials = CodexCredentialLoader.LoadFromFile(authFilePath);
        return FetchAsync(credentials, cancellationToken);
    }

    public Task<CodexOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken = default)
    {
        var defaultPath = CodexCredentialLoader.GetDefaultDesktopAuthPath();
        var credentials = CodexCredentialLoader.LoadFromFile(defaultPath);
        return FetchAsync(credentials, cancellationToken);
    }

    public static CodexOAuthUsageResult? ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? planType = null;
            double? primaryUsedPercent = null;
            DateTimeOffset? primaryResetsAt = null;
            int? primaryWindowSeconds = null;
            double? secondaryUsedPercent = null;
            DateTimeOffset? secondaryResetsAt = null;
            int? secondaryWindowSeconds = null;
            double? creditBalance = null;
            bool hasCredits = false;

            // Parse plan_type
            if (root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
            {
                planType = pt.GetString();
            }

            // Parse rate_limit
            if (root.TryGetProperty("rate_limit", out var rateLimit))
            {
                // Primary window (usually 5-hour/session)
                if (rateLimit.TryGetProperty("primary_window", out var primary))
                {
                    if (primary.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number)
                    {
                        primaryUsedPercent = up.GetDouble();
                    }
                    if (primary.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
                    {
                        primaryResetsAt = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
                    }
                    if (primary.TryGetProperty("limit_window_seconds", out var lws) && lws.ValueKind == JsonValueKind.Number)
                    {
                        primaryWindowSeconds = lws.GetInt32();
                    }
                }

                // Secondary window (usually weekly)
                if (rateLimit.TryGetProperty("secondary_window", out var secondary))
                {
                    if (secondary.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number)
                    {
                        secondaryUsedPercent = up.GetDouble();
                    }
                    if (secondary.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
                    {
                        secondaryResetsAt = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
                    }
                    if (secondary.TryGetProperty("limit_window_seconds", out var lws) && lws.ValueKind == JsonValueKind.Number)
                    {
                        secondaryWindowSeconds = lws.GetInt32();
                    }
                }
            }

            // Parse credits
            if (root.TryGetProperty("credits", out var credits))
            {
                if (credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True)
                {
                    hasCredits = true;
                }
                if (credits.TryGetProperty("balance", out var bal))
                {
                    if (bal.ValueKind == JsonValueKind.Number)
                    {
                        creditBalance = bal.GetDouble();
                    }
                    else if (bal.ValueKind == JsonValueKind.String && double.TryParse(bal.GetString(), out var balVal))
                    {
                        creditBalance = balVal;
                    }
                }
            }

            return new CodexOAuthUsageResult(
                planType,
                primaryUsedPercent,
                primaryResetsAt,
                primaryWindowSeconds,
                secondaryUsedPercent,
                secondaryResetsAt,
                secondaryWindowSeconds,
                hasCredits,
                creditBalance,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

public sealed record CodexOAuthUsageResult(
    string? PlanType,
    double? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetsAt,
    int? PrimaryWindowSeconds,
    double? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetsAt,
    int? SecondaryWindowSeconds,
    bool HasCredits,
    double? CreditBalance,
    DateTimeOffset FetchedAt);
