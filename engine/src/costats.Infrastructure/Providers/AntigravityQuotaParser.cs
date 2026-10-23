// Derived from public research and schema definitions in AntigravityQuotaSummaryParser.swift
// CodexBar 3a45dc7cf7c262fd26601fa0edfafddbf5ab465a, Copyright (c) 2026 Peter Steinberger.
// MIT License retained in THIRD_PARTY_NOTICES.md; see engine/ADAPTATIONS.md.

using System.Globalization;
using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public sealed record AntigravityQuotaResult(
    IReadOnlyList<NamedQuotaBucket> Buckets,
    string? SummaryDescription);

public static class AntigravityQuotaParser
{
    public static AntigravityQuotaResult ParseQuotaSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
            }

            // Handle wrapped structures:
            // 1. response: { groups: [...] }
            // 2. summary: { groups: [...] }
            // 3. command.data: { groups: [...] } (CLI usage report)
            JsonElement targetElement = root;
            if (TryGetObject(root, "response", out var resp))
            {
                targetElement = resp;
            }
            else if (TryGetObject(root, "summary", out var summ))
            {
                targetElement = summ;
            }
            else if (TryGetObject(root, "command", out var cmd))
            {
                if (TryGetObject(cmd, "data", out var cmdData))
                {
                    targetElement = cmdData;
                }
            }

            // Fall back to root if targetElement doesn't have groups or buckets
            if (!HasGroupsOrBuckets(targetElement) && HasGroupsOrBuckets(root))
            {
                targetElement = root;
            }

            var buckets = new List<NamedQuotaBucket>();
            string? summaryDescription = GetTrimmedString(targetElement, "description") ?? GetTrimmedString(root, "description");

            if (TryGetArray(targetElement, "groups", out var groupsProp) || TryGetArray(root, "groups", out groupsProp))
            {
                foreach (var group in groupsProp.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var groupName = GetTrimmedString(group, "displayName") ?? GetTrimmedString(group, "name");

                    if (TryGetArray(group, "buckets", out var groupBuckets))
                    {
                        foreach (var bucket in groupBuckets.EnumerateArray())
                        {
                            if (bucket.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var parsedBucket = ParseBucketElement(bucket, groupName);
                            if (parsedBucket is not null)
                            {
                                buckets.Add(parsedBucket);
                            }
                        }
                    }
                }
            }

            // Fallback: check for top-level or response-level "buckets" array (e.g. retrieveUserQuota fallback)
            if (buckets.Count == 0)
            {
                if (TryGetArray(targetElement, "buckets", out var directBuckets) || TryGetArray(root, "buckets", out directBuckets))
                {
                    foreach (var bucket in directBuckets.EnumerateArray())
                    {
                        if (bucket.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var parsedBucket = ParseBucketElement(bucket, null);
                        if (parsedBucket is not null)
                        {
                            buckets.Add(parsedBucket);
                        }
                    }
                }
            }

            return new AntigravityQuotaResult(buckets, summaryDescription);
        }
        catch
        {
            return new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
        }
    }

    public static AntigravityQuotaResult ParseGetUserStatusFallback(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
            }

            if (!TryGetObject(root, "userStatus", out var userStatus))
            {
                return new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
            }

            var buckets = new List<NamedQuotaBucket>();
            if (TryGetObject(userStatus, "cascadeModelConfigData", out var modelConfigData) &&
                TryGetArray(modelConfigData, "clientModelConfigs", out var configs))
            {
                foreach (var config in configs.EnumerateArray())
                {
                    if (config.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? label = GetTrimmedString(config, "label");
                    string? modelId = GetTrimmedString(config, "modelId");

                    var id = modelId ?? label;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var name = label ?? modelId ?? "Unknown Model";

                    float? usedPercent = null;
                    DateTimeOffset? resetTime = null;

                    if (TryGetObject(config, "quotaInfo", out var quotaInfo))
                    {
                        double? remFraction = ExtractRemainingFraction(quotaInfo);
                        usedPercent = CalculateUsedPercent(remFraction);

                        string? rt = GetTrimmedString(quotaInfo, "resetTime");
                        if (!string.IsNullOrWhiteSpace(rt) &&
                            DateTimeOffset.TryParse(rt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedReset))
                        {
                            resetTime = parsedReset;
                        }
                    }

                    buckets.Add(new NamedQuotaBucket(
                        Id: id,
                        Name: name,
                        UsedPercent: usedPercent,
                        UsedUnits: null,
                        LimitUnits: null,
                        ResetsAt: resetTime,
                        WindowDuration: null,
                        StatusSummary: null));
                }
            }

            return new AntigravityQuotaResult(buckets, null);
        }
        catch
        {
            return new AntigravityQuotaResult(Array.Empty<NamedQuotaBucket>(), null);
        }
    }

    private static NamedQuotaBucket? ParseBucketElement(JsonElement bucket, string? groupName)
    {
        if (bucket.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? bucketId = GetTrimmedString(bucket, "bucketId") ?? GetTrimmedString(bucket, "id");
        string? displayName = GetTrimmedString(bucket, "displayName") ?? GetTrimmedString(bucket, "name");
        string? modelId = GetTrimmedString(bucket, "modelId");
        string? desc = GetTrimmedString(bucket, "description") ?? GetTrimmedString(bucket, "resetDescription");
        string? windowStr = GetTrimmedString(bucket, "window") ?? GetTrimmedString(bucket, "windowDuration");

        // Support modelOrAlias.model or modelOrAlias.alias fallback
        string? moaModel = null;
        if (TryGetObject(bucket, "modelOrAlias", out var moa))
        {
            moaModel = GetTrimmedString(moa, "model") ?? GetTrimmedString(moa, "alias");
        }

        var id = bucketId ?? modelId ?? moaModel ?? displayName;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var label = displayName ?? moaModel ?? modelId ?? bucketId ?? "Quota";
        if (!string.IsNullOrWhiteSpace(groupName) && !label.Contains(groupName, StringComparison.OrdinalIgnoreCase))
        {
            label = $"{groupName} - {label}";
        }

        double? remFraction = ExtractRemainingFraction(bucket);
        float? usedPercent = CalculateUsedPercent(remFraction);

        DateTimeOffset? resetsAt = null;
        string? rtStr = GetTrimmedString(bucket, "resetTime") ?? GetTrimmedString(bucket, "resetsAt");
        if (!string.IsNullOrWhiteSpace(rtStr))
        {
            if (DateTimeOffset.TryParse(rtStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                resetsAt = parsed;
            }
        }

        TimeSpan? windowDuration = ParseWindowDuration(windowStr);

        return new NamedQuotaBucket(
            Id: id,
            Name: label,
            UsedPercent: usedPercent,
            UsedUnits: null,
            LimitUnits: null,
            ResetsAt: resetsAt,
            WindowDuration: windowDuration,
            StatusSummary: desc);
    }

    private static double? ExtractRemainingFraction(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 1. Direct remainingFraction
        if (element.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number)
        {
            if (rf.TryGetDouble(out var d))
            {
                return d;
            }
        }

        // 2. Direct fraction
        if (element.TryGetProperty("fraction", out var f) && f.ValueKind == JsonValueKind.Number)
        {
            if (f.TryGetDouble(out var d))
            {
                return d;
            }
        }

        // 3. remaining object or number
        if (element.TryGetProperty("remaining", out var rem))
        {
            if (rem.ValueKind == JsonValueKind.Number && rem.TryGetDouble(out var remNum))
            {
                return remNum;
            }

            if (rem.ValueKind == JsonValueKind.Object)
            {
                if (rem.TryGetProperty("remainingFraction", out var innerRf) && innerRf.ValueKind == JsonValueKind.Number)
                {
                    if (innerRf.TryGetDouble(out var d))
                    {
                        return d;
                    }
                }

                if (rem.TryGetProperty("fraction", out var innerF) && innerF.ValueKind == JsonValueKind.Number)
                {
                    if (innerF.TryGetDouble(out var d))
                    {
                        return d;
                    }
                }

                // Protobuf oneof JSON: { "case": "remainingFraction", "value": 0.8 }
                if (rem.TryGetProperty("case", out var caseProp) && caseProp.ValueKind == JsonValueKind.String &&
                    string.Equals(caseProp.GetString(), "remainingFraction", StringComparison.Ordinal) &&
                    rem.TryGetProperty("value", out var valProp) && valProp.ValueKind == JsonValueKind.Number)
                {
                    if (valProp.TryGetDouble(out var d))
                    {
                        return d;
                    }
                }
            }
        }

        return null;
    }

    private static float? CalculateUsedPercent(double? remainingFraction)
    {
        if (!remainingFraction.HasValue)
        {
            return null;
        }

        var rem = remainingFraction.Value;
        if (double.IsNaN(rem) || double.IsInfinity(rem) || rem < 0.0 || rem > 1.0)
        {
            return null;
        }

        return (float)Math.Clamp((1.0 - rem) * 100.0, 0.0, 100.0);
    }

    private static TimeSpan? ParseWindowDuration(string? windowStr)
    {
        if (string.IsNullOrWhiteSpace(windowStr))
        {
            return null;
        }

        try
        {
            var s = windowStr.Trim().ToLowerInvariant();

            // Normalize descriptive words to unit suffixes
            if (s.EndsWith("hours") || s.EndsWith("hour"))
                s = s.Replace("hours", "h").Replace("hour", "h").Trim();
            else if (s.EndsWith("days") || s.EndsWith("day"))
                s = s.Replace("days", "d").Replace("day", "d").Trim();
            else if (s.EndsWith("weeks") || s.EndsWith("week"))
                s = s.Replace("weeks", "w").Replace("week", "w").Trim();
            else if (s.EndsWith("minutes") || s.EndsWith("minute") || s.EndsWith("mins") || s.EndsWith("min"))
                s = s.Replace("minutes", "m").Replace("minute", "m").Replace("mins", "m").Replace("min", "m").Trim();
            else if (s.EndsWith("seconds") || s.EndsWith("second") || s.EndsWith("secs") || s.EndsWith("sec"))
                s = s.Replace("seconds", "s").Replace("second", "s").Replace("secs", "s").Replace("sec", "s").Trim();

            if (s.EndsWith("h") && double.TryParse(s[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var hours) &&
                IsValidPositiveNumber(hours))
            {
                return TimeSpan.FromHours(hours);
            }
            if (s.EndsWith("d") && double.TryParse(s[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var days) &&
                IsValidPositiveNumber(days))
            {
                return TimeSpan.FromDays(days);
            }
            if (s.EndsWith("w") && double.TryParse(s[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var weeks) &&
                IsValidPositiveNumber(weeks))
            {
                return TimeSpan.FromDays(weeks * 7);
            }
            if (s.EndsWith("m") && double.TryParse(s[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var mins) &&
                IsValidPositiveNumber(mins))
            {
                return TimeSpan.FromMinutes(mins);
            }
            if (s.EndsWith("s") && double.TryParse(s[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var secs) &&
                IsValidPositiveNumber(secs))
            {
                return TimeSpan.FromSeconds(secs);
            }

            // ISO 8601 duration: e.g. "PT5H", "P7D"
            if (s.StartsWith("p"))
            {
                var ts = System.Xml.XmlConvert.ToTimeSpan(windowStr.Trim());
                if (ts > TimeSpan.Zero)
                {
                    return ts;
                }
            }
        }
        catch
        {
            // Do not throw on huge or invalid duration strings
        }

        return null;
    }

    private static bool IsValidPositiveNumber(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value < 100_000;
    }

    private static bool HasGroupsOrBuckets(JsonElement el)
    {
        return el.ValueKind == JsonValueKind.Object &&
               ((el.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array) ||
                (el.TryGetProperty("buckets", out var b) && b.ValueKind == JsonValueKind.Array));
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out result) && result.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        result = default;
        return false;
    }

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out result) && result.ValueKind == JsonValueKind.Array)
        {
            return true;
        }
        result = default;
        return false;
    }

    private static string? GetTrimmedString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                return s.Trim();
            }
        }
        return null;
    }
}
