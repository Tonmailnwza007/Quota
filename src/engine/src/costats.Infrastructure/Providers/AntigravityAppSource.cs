using System.Diagnostics;
using System.Management;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Network;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public sealed record AntigravityProcessInfo(int Pid, string CsrfToken, IReadOnlyList<int> Ports)
{
    public override string ToString() =>
        $"AntigravityProcessInfo {{ Pid = {Pid}, Ports = [{string.Join(", ", Ports)}], HasCsrfToken = {!string.IsNullOrEmpty(CsrfToken)} }}";
}

public interface IAntigravityProcessLocator
{
    AntigravityProcessInfo? LocateProcess(AppSettings? settings);
}

public sealed class FallbackProcessLocator : IAntigravityProcessLocator
{
    public AntigravityProcessInfo? LocateProcess(AppSettings? settings) => null;
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsAntigravityProcessLocator : IAntigravityProcessLocator
{
    private static readonly Regex CsrfTokenRegex = new(
        @"(?:--?csrf_token|--?csrf-token)[=\s]+""?([a-zA-Z0-9_-]+)""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TOKEN_QUERY = 0x0008;

    public AntigravityProcessInfo? LocateProcess(AppSettings? settings)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        try
        {
            var defaultInstallDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Antigravity");

            var baseDir = !string.IsNullOrWhiteSpace(settings?.AntigravityAppPath)
                ? settings.AntigravityAppPath.Trim()
                : defaultInstallDir;

            string expectedExePath;
            if (baseDir.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                expectedExePath = Path.GetFullPath(baseDir);
            }
            else
            {
                expectedExePath = Path.GetFullPath(Path.Combine(baseDir, "resources", "bin", "language_server.exe"));
            }

            var processes = Process.GetProcessesByName("language_server");

            foreach (var proc in processes)
            {
                using (proc)
                {
                    // Fail closed if owner SID or session does not match current user
                    if (!IsCurrentSessionUser(proc))
                    {
                        continue;
                    }

                    // Fail closed on inaccessible or unverified exe path
                    string? exePath = null;
                    try
                    {
                        exePath = proc.MainModule?.FileName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        continue;
                    }

                    var resolvedExe = Path.GetFullPath(exePath);
                    if (!string.Equals(resolvedExe, expectedExePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var cmdLine = GetProcessCommandLine(proc.Id);
                    if (string.IsNullOrWhiteSpace(cmdLine))
                    {
                        continue;
                    }

                    var match = CsrfTokenRegex.Match(cmdLine);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var csrfToken = match.Groups[1].Value;
                    var ports = TcpEndpointHelper.GetListeningPortsForProcess(proc.Id);
                    if (ports.Count > 0)
                    {
                        return new AntigravityProcessInfo(proc.Id, csrfToken, ports);
                    }
                }
            }
        }
        catch
        {
            // Do not log command line or tokens
        }

        return null;
    }

    private static bool IsCurrentSessionUser(Process proc)
    {
        try
        {
            if (proc.SessionId != Process.GetCurrentProcess().SessionId)
            {
                return false;
            }

            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(proc.Handle, TOKEN_QUERY, out tokenHandle) || tokenHandle == IntPtr.Zero)
                {
                    return false;
                }

                using var identity = new WindowsIdentity(tokenHandle);
                var currentSid = WindowsIdentity.GetCurrent().User;
                return identity.User is not null && currentSid is not null && identity.User.Equals(currentSid);
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    CloseHandle(tokenHandle);
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? GetProcessCommandLine(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            using var collection = searcher.Get();
            foreach (var item in collection)
            {
                using (item)
                {
                    return item["CommandLine"]?.ToString();
                }
            }
        }
        catch
        {
            // Process command line inaccessible
        }

        return null;
    }
}

public sealed class AntigravityAppSource : ISignalSource
{
    private readonly AppSettings? _settings;
    private readonly IAntigravityProcessLocator _processLocator;
    private readonly HttpClient _loopbackClient;

    public ProviderProfile Profile => ProviderCatalog.Antigravity;

    public AntigravityAppSource(
        AppSettings? settings = null,
        IAntigravityProcessLocator? processLocator = null,
        HttpClient? loopbackClient = null)
    {
        _settings = settings;
        _processLocator = processLocator ?? (OperatingSystem.IsWindows() ? new WindowsAntigravityProcessLocator() : new FallbackProcessLocator());

        if (loopbackClient is not null)
        {
            _loopbackClient = loopbackClient;
        }
        else
        {
            // Request-aware certificate validation callback strictly scoped to https://127.0.0.1 or localhost
            // Disallow auto-redirects so CSRF tokens cannot be leaked across redirects
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (request, cert, chain, sslPolicyErrors) =>
                {
                    if (request?.RequestUri is not null)
                    {
                        return request.RequestUri.Scheme == Uri.UriSchemeHttps &&
                               (request.RequestUri.Host == "127.0.0.1" || request.RequestUri.Host == "localhost");
                    }
                    return false;
                }
            };

            _loopbackClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };
        }
    }

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        var processInfo = _processLocator.LocateProcess(_settings);
        if (processInfo is null || processInfo.Ports.Count == 0)
        {
            return new ProviderReading(
                Usage: null,
                Identity: null,
                StatusSummary: "Antigravity is not running",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.LocalLog);
        }

        // Probe listening ports
        foreach (var port in processInfo.Ports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var summaryUri = new Uri($"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary");
                var statusUri = new Uri($"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus");

                var metadataPayload = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\",\"ideVersion\":\"unknown\"}}";

                // Fetch quota summary
                using var summaryRequest = new HttpRequestMessage(HttpMethod.Post, summaryUri)
                {
                    Content = new StringContent(metadataPayload, Encoding.UTF8, "application/json")
                };
                summaryRequest.Headers.Add("Connect-Protocol-Version", "1");
                summaryRequest.Headers.Add("X-Codeium-Csrf-Token", processInfo.CsrfToken);

                AntigravityQuotaResult quotaResult = new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
                bool summarySuccess = false;

                try
                {
                    using var summaryResponse = await _loopbackClient.SendAsync(summaryRequest, cancellationToken).ConfigureAwait(false);
                    if (summaryResponse.IsSuccessStatusCode)
                    {
                        var summaryJson = await summaryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        quotaResult = AntigravityQuotaParser.ParseQuotaSummary(summaryJson);
                        summarySuccess = true;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Proceed to GetUserStatus
                }

                // Fetch user status for identity and fallback
                string? email = null;
                string? planName = null;
                bool statusSuccess = false;
                try
                {
                    using var statusRequest = new HttpRequestMessage(HttpMethod.Post, statusUri)
                    {
                        Content = new StringContent(metadataPayload, Encoding.UTF8, "application/json")
                    };
                    statusRequest.Headers.Add("Connect-Protocol-Version", "1");
                    statusRequest.Headers.Add("X-Codeium-Csrf-Token", processInfo.CsrfToken);

                    using var statusResponse = await _loopbackClient.SendAsync(statusRequest, cancellationToken).ConfigureAwait(false);
                    if (statusResponse.IsSuccessStatusCode)
                    {
                        statusSuccess = true;
                        var statusJson = await statusResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var statusDoc = JsonDocument.Parse(statusJson);
                        if (statusDoc.RootElement.TryGetProperty("userStatus", out var us) && us.ValueKind == JsonValueKind.Object)
                        {
                            email = us.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : null;
                            if (us.TryGetProperty("planStatus", out var ps) && ps.ValueKind == JsonValueKind.Object &&
                                ps.TryGetProperty("planInfo", out var pi) && pi.ValueKind == JsonValueKind.Object)
                            {
                                planName = pi.TryGetProperty("planName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
                            }
                            if (string.IsNullOrWhiteSpace(planName) && us.TryGetProperty("userTier", out var ut) && ut.ValueKind == JsonValueKind.Object)
                            {
                                planName = ut.TryGetProperty("name", out var utn) && utn.ValueKind == JsonValueKind.String ? utn.GetString() : null;
                            }

                            // If summary had no buckets or failed, use status model configs fallback
                            if (quotaResult.Buckets.Count == 0)
                            {
                                quotaResult = AntigravityQuotaParser.ParseGetUserStatusFallback(statusJson);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Fall back to quota result without full status identity
                }

                if (!summarySuccess && !statusSuccess)
                {
                    continue;
                }

                var identity = new IdentityCard(
                    ProviderId: Profile.ProviderId,
                    DisplayName: Profile.DisplayName,
                    Email: email,
                    Org: null,
                    Plan: !string.IsNullOrWhiteSpace(planName) ? planName : null,
                    LoginMethod: "App");

                var usage = new UsagePulse(
                    ProviderId: Profile.ProviderId,
                    CapturedAt: now,
                    SessionUsed: null,
                    SessionLimit: null,
                    WeekUsed: null,
                    WeekLimit: null,
                    SpendingBucket: null,
                    Consumption: null,
                    SessionWindow: null,
                    WeekWindow: null,
                    QuotaBuckets: quotaResult.Buckets,
                    AccountEmail: email,
                    AccountId: null); // Email is not the Google subject identifier used by background OAuth.

                var statusSummary = quotaResult.Buckets.Count > 0
                    ? $"Live ({quotaResult.Buckets.Count} quotas)"
                    : "Live (limits not available)";

                return new ProviderReading(
                    Usage: usage,
                    Identity: identity,
                    StatusSummary: statusSummary,
                    CapturedAt: now,
                    Confidence: ReadingConfidence.High,
                    Source: ReadingSource.Api);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try next listening port
            }
        }

        return new ProviderReading(
            Usage: null,
            Identity: null,
            StatusSummary: "Antigravity service unreachable",
            CapturedAt: now,
            Confidence: ReadingConfidence.Low,
            Source: ReadingSource.LocalLog);
    }
}
