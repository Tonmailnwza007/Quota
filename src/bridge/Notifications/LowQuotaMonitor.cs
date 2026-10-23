using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Costats.Bridge.Notifications;

public sealed record QuotaAlert(string Title, string Message);

/// <summary>Evaluates display-only readings. History contains hashes, bands and timestamps, never display identities.</summary>
public sealed class LowQuotaMonitor
{
    private const int MaximumEntries = 1024;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResetJitter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(45);
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private History? _history;

    public LowQuotaMonitor(string historyPath, TimeProvider? timeProvider = null)
        => (_path, _clock) = (Path.GetFullPath(historyPath), timeProvider ?? TimeProvider.System);

    public async Task<IReadOnlyList<QuotaAlert>> EvaluateAsync(DashboardDto state, NotificationSettingsDto settings, CancellationToken ct = default)
    {
        if (!settings.Enabled || state.IsRefreshing || state.Mode != "live") return [];
        if (settings.CriticalPercent < 0 || settings.CriticalPercent >= settings.WarningPercent || settings.WarningPercent > 100)
            throw new ArgumentException("Invalid quota notification thresholds.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _history ??= await LoadAsync(ct).ConfigureAwait(false);
            var next = _history.Clone();
            var now = _clock.GetUtcNow();
            var valid = state.Providers.Where(p => p.Status == "ok" && !string.Equals(p.SourceLabel, "Local logs", StringComparison.OrdinalIgnoreCase) && TryCaptured(p.LastUpdated, now, out _) &&
                (Measured(p.SessionProgress) || Measured(p.WeeklyProgress) || p.NamedBuckets.Any(bucket => Measured(bucket.Percentage)))).ToArray();
            var changed = UpdateAliases(next, valid, now);
            var candidates = valid.SelectMany(p => Candidates(next, p)).GroupBy(p => Key(p.Account, p.Quota))
                .Select(group => group.OrderByDescending(p => p.Captured).ThenByDescending(p => p.Used).First()).Take(MaximumEntries).ToArray();
            var alerts = new Dictionary<string, List<(Candidate Candidate, bool Critical)>>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var key = Key(candidate.Account, candidate.Quota);
                if (!next.Entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry { Account = candidate.Account, Quota = candidate.Quota, CycleReset = candidate.Reset };
                    next.Entries.Add(key, entry);
                    changed = true;
                }
                if (candidate.Captured < entry.LastCapture) continue;
                var distinct = candidate.Captured > entry.LastCapture;
                var remaining = 100 - candidate.Used;
                if (distinct)
                {
                    // A different reset alone is not a cycle: the old reset must have elapsed in a newly captured reading.
                    if (entry.CycleReset is { } prior && candidate.Reset is { } upcoming && candidate.Captured >= prior &&
                        now >= prior && upcoming > prior + ResetJitter && upcoming > candidate.Captured)
                    {
                        entry.Bands.Clear(); entry.Recovery = 0; entry.CycleReset = upcoming;
                    }
                    else if (entry.CycleReset is null && candidate.Reset is not null) entry.CycleReset = candidate.Reset;

                    if (candidate.Reset is null && remaining > Math.Min(100, settings.WarningPercent + 2))
                    {
                        entry.Recovery = Math.Min(2, entry.Recovery + 1);
                        if (entry.Recovery == 2) { entry.Bands.Clear(); entry.CycleReset = null; }
                    }
                    else entry.Recovery = 0;
                    entry.LastCapture = candidate.Captured; entry.LastSeen = now;
                    changed = true;
                }
                var critical = remaining <= settings.CriticalPercent;
                if (remaining > settings.WarningPercent) continue;
                var band = (critical ? "c:" : "w:") + (critical ? settings.CriticalPercent : settings.WarningPercent).ToString(CultureInfo.InvariantCulture);
                if (entry.Bands.Add(band))
                {
                    if (!alerts.TryGetValue(candidate.Account, out var accountAlerts)) alerts[candidate.Account] = accountAlerts = [];
                    accountAlerts.Add((candidate, critical));
                    changed = true;
                }
                // A critical jump consumes the warning too, so fluctuations cannot produce a later warning in the same cycle.
                if (critical && entry.Bands.Add("w:" + settings.WarningPercent.ToString(CultureInfo.InvariantCulture))) changed = true;
            }
            changed |= Prune(next, now, candidates.Select(candidate => Key(candidate.Account, candidate.Quota)).ToHashSet(StringComparer.Ordinal));
            if (changed)
            {
                await SaveAsync(next, ct).ConfigureAwait(false);
                _history = next; // Commit in memory only after the atomic on-disk commit succeeds.
            }
            return alerts.Values.Select(group =>
            {
                var ordered = group.OrderByDescending(a => a.Critical).ThenByDescending(a => a.Candidate.Used).ToArray();
                var provider = SafeLabel(ordered[0].Candidate.Provider.DisplayName, FamilyLabel(ordered[0].Candidate.Provider.Family), ordered[0].Candidate.Provider, 60);
                var title = (ordered.Any(a => a.Critical) ? "Quota critical · " : "Quota low · ") + provider;
                var limits = ordered.Take(3).Select(a => Describe(a.Candidate));
                var message = string.Join("; ", limits);
                if (ordered.Length > 3) message += $"; +{ordered.Length - 3} other limits";
                return new QuotaAlert(title, message.Length <= 420 ? message : message[..417] + "...");
            }).ToArray();
        }
        finally { _gate.Release(); }
    }

    private static bool TryCaptured(string text, DateTimeOffset now, out DateTimeOffset captured)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out captured) &&
            captured >= now - MaximumAge && captured <= now.AddMinutes(1);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Key(string account, string quota) => Hash(account + "|" + quota);
    private static string? IdentityHash(ProviderDto provider) => string.IsNullOrWhiteSpace(provider.AccountId) ? null : Hash(provider.Family + "|id|" + provider.AccountId.Trim());
    private static string? EmailHash(ProviderDto provider) => string.IsNullOrWhiteSpace(provider.AccountEmail) ? null : Hash(provider.Family + "|email|" + provider.AccountEmail.Trim().ToLowerInvariant());

    private static bool UpdateAliases(History history, ProviderDto[] providers, DateTimeOffset now)
    {
        var changed = false;
        foreach (var provider in providers)
        {
            if (IdentityHash(provider) is not { } account || EmailHash(provider) is not { } email) continue;
            var captured = DateTimeOffset.Parse(provider.LastUpdated, CultureInfo.InvariantCulture);
            if (!history.Aliases.TryGetValue(email, out var alias))
            {
                history.Aliases[email] = alias = new Alias { Account = account, LastCapture = captured, LastSeen = now };
                changed = true;
            }
            else
            {
                if (alias.Account is not null && alias.Account != account) { alias.Account = null; changed = true; }
                if (captured > alias.LastCapture) { alias.LastCapture = captured; alias.LastSeen = now; changed = true; }
            }
            if (alias.Account != account) continue;
            // Preserve warnings first observed with email-only identity when a stronger account ID becomes available.
            foreach (var old in history.Entries.Values.Where(value => value.Account == email).ToArray())
            {
                history.Entries.Remove(Key(old.Account, old.Quota));
                var targetKey = Key(account, old.Quota);
                if (history.Entries.TryGetValue(targetKey, out var target))
                {
                    target.Bands.UnionWith(old.Bands);
                    if (old.LastCapture > target.LastCapture) { target.LastCapture = old.LastCapture; target.LastSeen = old.LastSeen; }
                }
                else { old.Account = account; history.Entries[targetKey] = old; }
                changed = true;
            }
        }
        return changed;
    }

    private static IEnumerable<Candidate> Candidates(History history, ProviderDto provider)
    {
        var account = IdentityHash(provider);
        if (account is null && EmailHash(provider) is { } email)
            account = history.Aliases.TryGetValue(email, out var alias) && alias.Account is not null ? alias.Account : email;
        account ??= Hash(provider.Family + "|provider|" + provider.Id);
        var captured = DateTimeOffset.Parse(provider.LastUpdated, CultureInfo.InvariantCulture);
        if (Measured(provider.SessionProgress)) yield return Make("window:session", "Session", provider.SessionProgress!.Value, provider.SessionResetAt);
        if (Measured(provider.WeeklyProgress)) yield return Make("window:weekly", "Weekly", provider.WeeklyProgress!.Value, provider.WeeklyResetAt);
        foreach (var bucket in provider.NamedBuckets)
            if (!string.IsNullOrWhiteSpace(bucket.Id) && Measured(bucket.Percentage))
                yield return Make("bucket:" + bucket.Id.Trim(), bucket.Name, bucket.Percentage!.Value, bucket.ResetAt);
        Candidate Make(string quota, string label, double used, string? reset) => new(account, Hash(quota), label, used, captured,
            DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null, provider);
    }
    private static bool Measured(double? used) => used is >= 0 and <= 100 && double.IsFinite(used.Value);
    private static string FamilyLabel(string family) => family switch { "codex" => "Codex", "antigravity" => "Antigravity", "claude" => "Claude", "copilot" => "Copilot", _ => "Provider" };
    private static string SafeLabel(string? value, string fallback, ProviderDto provider, int max)
    {
        if (value is null || value.Contains('@') || value.Contains("ya29.", StringComparison.OrdinalIgnoreCase)) return fallback;
        foreach (var identity in new[] { provider.AccountId, provider.AccountEmail })
            if (!string.IsNullOrWhiteSpace(identity)) value = value.Replace(identity, "", StringComparison.OrdinalIgnoreCase);
        var safe = StateMapper.DisplayText(value, 256);
        if (string.IsNullOrWhiteSpace(safe)) return fallback;
        return safe.Length <= max ? safe : safe[..(max - 3)] + "...";
    }
    private static string Describe(Candidate candidate)
    {
        var label = SafeLabel(candidate.Label, "Quota", candidate.Provider, 55);
        var remaining = (100 - candidate.Used).ToString("0.##", CultureInfo.InvariantCulture);
        var reset = candidate.Reset is { } time ? " (resets " + time.ToUniversalTime().ToString("dd MMM HH:mm 'UTC'", CultureInfo.InvariantCulture) + ")" : "";
        return $"{label}: {remaining}% remaining{reset}";
    }

    private static bool Prune(History history, DateTimeOffset now, HashSet<string> active)
    {
        var changed = false;
        foreach (var key in history.Entries.Where(pair => pair.Value.LastSeen < now - Retention).Select(pair => pair.Key).ToArray()) { history.Entries.Remove(key); changed = true; }
        foreach (var key in history.Entries.OrderByDescending(pair => active.Contains(pair.Key)).ThenByDescending(pair => pair.Value.LastSeen).Skip(MaximumEntries).Select(pair => pair.Key).ToArray()) { history.Entries.Remove(key); changed = true; }
        foreach (var key in history.Aliases.Where(pair => pair.Value.LastSeen < now - Retention).Select(pair => pair.Key).ToArray()) { history.Aliases.Remove(key); changed = true; }
        foreach (var key in history.Aliases.OrderByDescending(pair => pair.Value.LastSeen).Skip(MaximumEntries).Select(pair => pair.Key).ToArray()) { history.Aliases.Remove(key); changed = true; }
        return changed;
    }

    private async Task<History> LoadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(_path)) return new();
            if (new FileInfo(_path).Length > 1024 * 1024) throw new IOException();
            var history = JsonSerializer.Deserialize<History>(await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false), WireJson.Options) ?? throw new IOException();
            if (history.Version != 1 || history.Entries is null || history.Aliases is null || history.Entries.Count > MaximumEntries || history.Aliases.Count > MaximumEntries) throw new IOException();
            bool IsHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
            foreach (var (key, entry) in history.Entries)
                if (entry is null || !IsHash(key) || !IsHash(entry.Account) || !IsHash(entry.Quota) || entry.Bands is null || entry.Bands.Count > 202 ||
                    entry.Recovery is < 0 or > 2 || entry.Bands.Any(band => !Regex.IsMatch(band, @"^[wc]:(?:[0-9]|[1-9][0-9]|100)$"))) throw new IOException();
            foreach (var (key, alias) in history.Aliases)
                if (!IsHash(key) || alias is null || alias.Account is { } account && !IsHash(account)) throw new IOException();
            return history;
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new IOException("Quota notification history could not be loaded."); }
    }
    private async Task SaveAsync(History history, CancellationToken ct)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(history, WireJson.Options), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new IOException("Quota notification history could not be saved."); }
        finally { if (temporary is not null) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } } }
    }

    private sealed record Candidate(string Account, string Quota, string Label, double Used, DateTimeOffset Captured, DateTimeOffset? Reset, ProviderDto Provider);
    private sealed class History
    {
        public History() { }
        public int Version { get; set; } = 1;
        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Alias> Aliases { get; set; } = new(StringComparer.Ordinal);
        public History Clone() => new() { Entries = Entries.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal), Aliases = Aliases.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal) };
    }
    private sealed class Entry
    {
        public Entry() { }
        public string Account { get; set; } = "";
        public string Quota { get; set; } = "";
        public DateTimeOffset? CycleReset { get; set; }
        public DateTimeOffset LastCapture { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public int Recovery { get; set; }
        public HashSet<string> Bands { get; set; } = new(StringComparer.Ordinal);
        public Entry Clone() => new() { Account = Account, Quota = Quota, CycleReset = CycleReset, LastCapture = LastCapture, LastSeen = LastSeen, Recovery = Recovery, Bands = new(Bands, StringComparer.Ordinal) };
    }
    private sealed class Alias
    {
        public Alias() { }
        public string? Account { get; set; }
        public DateTimeOffset LastCapture { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public Alias Clone() => new() { Account = Account, LastCapture = LastCapture, LastSeen = LastSeen };
    }
}
