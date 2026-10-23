using System.Globalization;
using costats.Infrastructure.Providers;

namespace Costats.Bridge.Tests;

public sealed class AntigravityQuotaSchemaTests
{
    [Fact]
    public void ParseQuotaSummary_RealisticWrappedResponse_ParsesGroupedBuckets()
    {
        const string json = """
        {
          "response": {
            "description": "Antigravity Active Tier",
            "groups": [
              {
                "displayName": "Session Quota",
                "description": "Rolling window per session",
                "buckets": [
                  {
                    "bucketId": "gemini-1.5-pro",
                    "displayName": "Gemini 1.5 Pro",
                    "window": "5h",
                    "resetTime": "2026-10-18T20:00:00Z",
                    "remainingFraction": 0.40,
                    "description": "40% quota remaining"
                  },
                  {
                    "id": "gemini-flash",
                    "name": "Gemini 2.5 Flash",
                    "window": "5h",
                    "resetTime": "2026-10-18T19:30:00Z",
                    "remainingFraction": 0.85
                  }
                ]
              },
              {
                "name": "Weekly Limit",
                "buckets": [
                  {
                    "bucketId": "gemini-1.5-pro-weekly",
                    "displayName": "Gemini 1.5 Pro",
                    "window": "7d",
                    "resetTime": "2026-10-25T12:00:00Z",
                    "remainingFraction": 0.95
                  }
                ]
              }
            ]
          }
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);

        Assert.Equal("Antigravity Active Tier", result.SummaryDescription);
        Assert.Equal(3, result.Buckets.Count);

        // Bucket 0: Session - Gemini 1.5 Pro
        var b0 = result.Buckets[0];
        Assert.Equal("gemini-1.5-pro", b0.Id);
        Assert.Equal("Session Quota - Gemini 1.5 Pro", b0.Name);
        Assert.Equal(60.0f, b0.UsedPercent);
        Assert.Equal(TimeSpan.FromHours(5), b0.WindowDuration);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T20:00:00Z", CultureInfo.InvariantCulture), b0.ResetsAt);
        Assert.Equal("40% quota remaining", b0.StatusSummary);

        // Bucket 1: Session - Gemini 2.5 Flash
        var b1 = result.Buckets[1];
        Assert.Equal("gemini-flash", b1.Id);
        Assert.Equal("Session Quota - Gemini 2.5 Flash", b1.Name);
        Assert.Equal(15.0f, b1.UsedPercent);
        Assert.Equal(TimeSpan.FromHours(5), b1.WindowDuration);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T19:30:00Z", CultureInfo.InvariantCulture), b1.ResetsAt);

        // Bucket 2: Weekly Limit - Gemini 1.5 Pro
        var b2 = result.Buckets[2];
        Assert.Equal("gemini-1.5-pro-weekly", b2.Id);
        Assert.Equal("Weekly Limit - Gemini 1.5 Pro", b2.Name);
        Assert.Equal(5.0f, b2.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), b2.WindowDuration);
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T12:00:00Z", CultureInfo.InvariantCulture), b2.ResetsAt);
    }

    [Fact]
    public void ParseQuotaSummary_UnwrappedResponse_ParsesGroupsDirectly()
    {
        const string json = """
        {
          "description": "Direct Plan",
          "groups": [
            {
              "displayName": "Models",
              "buckets": [
                {
                  "bucketId": "claude-sonnet",
                  "displayName": "Claude 3.7 Sonnet",
                  "remainingFraction": 0.50
                }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);

        Assert.Equal("Direct Plan", result.SummaryDescription);
        var b = Assert.Single(result.Buckets);
        Assert.Equal("claude-sonnet", b.Id);
        Assert.Equal("Models - Claude 3.7 Sonnet", b.Name);
        Assert.Equal(50.0f, b.UsedPercent);
    }

    [Fact]
    public void ParseQuotaSummary_SummaryWrapper_ParsesCorrectly()
    {
        const string json = """
        {
          "summary": {
            "description": "Summary Nested",
            "groups": [
              {
                "displayName": "Core",
                "buckets": [
                  {
                    "bucketId": "b1",
                    "displayName": "Core Model",
                    "remainingFraction": 0.20
                  }
                ]
              }
            ]
          }
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);

        Assert.Equal("Summary Nested", result.SummaryDescription);
        var b = Assert.Single(result.Buckets);
        Assert.Equal("b1", b.Id);
        Assert.Equal(80.0f, b.UsedPercent);
    }

    [Fact]
    public void ParseQuotaSummary_CliUsageReportWrapper_ParsesCommandData()
    {
        const string json = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "description": "CLI Active Usage",
              "groups": [
                {
                  "displayName": "CLI Group",
                  "buckets": [
                    {
                      "bucketId": "cli-model",
                      "displayName": "CLI Fast Model",
                      "remainingFraction": 0.10
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);

        Assert.Equal("CLI Active Usage", result.SummaryDescription);
        var b = Assert.Single(result.Buckets);
        Assert.Equal("cli-model", b.Id);
        Assert.Equal(90.0f, b.UsedPercent);
    }

    [Fact]
    public void ParseQuotaSummary_DirectBucketsFallback_ParsesFlatBuckets()
    {
        const string json = """
        {
          "description": "Flat Buckets Fallback",
          "buckets": [
            {
              "bucketId": "flat-1",
              "displayName": "Flat Bucket",
              "remainingFraction": 0.33
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);

        Assert.Equal("Flat Buckets Fallback", result.SummaryDescription);
        var b = Assert.Single(result.Buckets);
        Assert.Equal("flat-1", b.Id);
        Assert.Equal("Flat Bucket", b.Name);
        Assert.Equal(67.0f, MathF.Round(b.UsedPercent!.Value, 1));
    }

    [Fact]
    public void ParseQuotaSummary_RemainingFractions_ZeroVsOneVsPartial()
    {
        const string json = """
        {
          "groups": [
            {
              "displayName": "Boundary Tests",
              "buckets": [
                { "bucketId": "zero-remaining", "remainingFraction": 0.0 },
                { "bucketId": "full-remaining", "remainingFraction": 1.0 },
                { "bucketId": "quarter-remaining", "remainingFraction": 0.25 }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);
        Assert.Equal(3, result.Buckets.Count);

        // remaining = 0 => 100% USED
        Assert.Equal(100.0f, result.Buckets[0].UsedPercent);

        // remaining = 1 => 0% USED
        Assert.Equal(0.0f, result.Buckets[1].UsedPercent);

        // remaining = 0.25 => 75% USED
        Assert.Equal(75.0f, result.Buckets[2].UsedPercent);
    }

    [Fact]
    public void ParseQuotaSummary_ProtobufOneofAndNestedRemaining_ParsesFraction()
    {
        const string json = """
        {
          "groups": [
            {
              "displayName": "Protobuf Quota",
              "buckets": [
                {
                  "bucketId": "oneof-case",
                  "remaining": {
                    "case": "remainingFraction",
                    "value": 0.70
                  }
                },
                {
                  "bucketId": "nested-fraction",
                  "remaining": {
                    "remainingFraction": 0.15
                  }
                },
                {
                  "bucketId": "direct-num-remaining",
                  "remaining": 0.40
                }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);
        Assert.Equal(3, result.Buckets.Count);

        // 1 - 0.70 = 0.30 => 30% USED
        Assert.Equal(30.0f, result.Buckets[0].UsedPercent);

        // 1 - 0.15 = 0.85 => 85% USED
        Assert.Equal(85.0f, result.Buckets[1].UsedPercent);

        // 1 - 0.40 = 0.60 => 60% USED
        Assert.Equal(60.0f, result.Buckets[2].UsedPercent);
    }

    [Fact]
    public void ParseQuotaSummary_UnknownVsRealZero_NullOrMissingRemainsNull()
    {
        const string json = """
        {
          "groups": [
            {
              "displayName": "Unknown Quotas",
              "buckets": [
                { "bucketId": "missing-fraction", "displayName": "Missing" },
                { "bucketId": "null-fraction", "remainingFraction": null },
                { "bucketId": "null-remaining-obj", "remaining": null },
                { "bucketId": "string-fraction", "remainingFraction": "0.5" },
                { "bucketId": "unmatched-oneof", "remaining": { "case": "otherOneof", "value": 0.5 } }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);
        Assert.Equal(5, result.Buckets.Count);

        // None of these should ever be treated as healthy 0%
        foreach (var bucket in result.Buckets)
        {
            Assert.Null(bucket.UsedPercent);
        }
    }

    [Fact]
    public void ParseQuotaSummary_OutOfRangeOrNonFinite_Unavailable()
    {
        const string json = """
        {
          "groups": [
            {
              "displayName": "Out of Range",
              "buckets": [
                { "bucketId": "negative-fraction", "remainingFraction": -0.05 },
                { "bucketId": "excess-fraction", "remainingFraction": 1.25 },
                { "bucketId": "huge-negative", "remainingFraction": -999.0 },
                { "bucketId": "huge-positive", "remainingFraction": 1000.0 }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);
        Assert.Equal(4, result.Buckets.Count);

        foreach (var bucket in result.Buckets)
        {
            Assert.Null(bucket.UsedPercent);
        }
    }

    [Fact]
    public void ParseQuotaSummary_MalformedSiblings_SkippedWithoutLosingValidBuckets()
    {
        const string json = """
        {
          "groups": [
            123,
            "not a group object",
            null,
            true,
            { "description": "Group without buckets array" },
            { "displayName": "Group with string buckets", "buckets": "not an array" },
            {
              "displayName": "Surviving Group",
              "buckets": [
                null,
                456,
                "bad bucket",
                {},
                { "window": "invalid" },
                {
                  "bucketId": "valid-1",
                  "displayName": "Model One",
                  "remainingFraction": 0.20
                },
                {
                  "bucketId": "valid-2",
                  "modelId": "model-two",
                  "remainingFraction": 0.80
                }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);

        Assert.Equal(2, result.Buckets.Count);
        Assert.Equal("valid-1", result.Buckets[0].Id);
        Assert.Equal("Surviving Group - Model One", result.Buckets[0].Name);
        Assert.Equal(80.0f, result.Buckets[0].UsedPercent);

        Assert.Equal("valid-2", result.Buckets[1].Id);
        Assert.Equal("Surviving Group - model-two", result.Buckets[1].Name);
        Assert.Equal(20.0f, result.Buckets[1].UsedPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("12345")]
    [InlineData("\"just a string\"")]
    [InlineData("true")]
    [InlineData("{ invalid json }")]
    [InlineData("{\"response\": null}")]
    [InlineData("{\"response\": []}")]
    [InlineData("{\"response\": 123}")]
    public void ParseQuotaSummary_MalformedRoots_NoExceptions(string malformedJson)
    {
        var result = AntigravityQuotaParser.ParseQuotaSummary(malformedJson);
        Assert.Empty(result.Buckets);
        Assert.Null(result.SummaryDescription);
    }

    [Fact]
    public void ParseQuotaSummary_WindowDuration_VariationsAndOverflowResistance()
    {
        const string json = """
        {
          "groups": [
            {
              "displayName": "Windows",
              "buckets": [
                { "bucketId": "w-5h", "window": "5h" },
                { "bucketId": "w-7d", "window": "7d" },
                { "bucketId": "w-1w", "window": "1w" },
                { "bucketId": "w-30m", "window": "30m" },
                { "bucketId": "w-60s", "window": "60s" },
                { "bucketId": "w-words-h", "window": "5 hours" },
                { "bucketId": "w-words-d", "window": "7 days" },
                { "bucketId": "w-iso", "window": "PT5H" },
                { "bucketId": "w-huge", "window": "999999999999999999999999999999999999999999999999999999999999999999999999999h" },
                { "bucketId": "w-negative", "window": "-5h" },
                { "bucketId": "w-zero", "window": "0h" },
                { "bucketId": "w-invalid", "window": "not-a-duration" }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);
        Assert.Equal(12, result.Buckets.Count);

        Assert.Equal(TimeSpan.FromHours(5), result.Buckets[0].WindowDuration);
        Assert.Equal(TimeSpan.FromDays(7), result.Buckets[1].WindowDuration);
        Assert.Equal(TimeSpan.FromDays(7), result.Buckets[2].WindowDuration);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Buckets[3].WindowDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Buckets[4].WindowDuration);
        Assert.Equal(TimeSpan.FromHours(5), result.Buckets[5].WindowDuration);
        Assert.Equal(TimeSpan.FromDays(7), result.Buckets[6].WindowDuration);
        Assert.Equal(TimeSpan.FromHours(5), result.Buckets[7].WindowDuration);

        // Huge / negative / zero / invalid must be safely null without throwing
        Assert.Null(result.Buckets[8].WindowDuration);
        Assert.Null(result.Buckets[9].WindowDuration);
        Assert.Null(result.Buckets[10].WindowDuration);
        Assert.Null(result.Buckets[11].WindowDuration);
    }

    [Fact]
    public void ParseQuotaSummary_ResetTime_DoesNotInventTimestamp()
    {
        const string json = """
        {
          "groups": [
            {
              "displayName": "Resets",
              "buckets": [
                { "bucketId": "valid-iso", "resetTime": "2026-10-18T22:30:00Z" },
                { "bucketId": "invalid-str", "resetTime": "yesterday at noon" },
                { "bucketId": "empty-str", "resetTime": "" },
                { "bucketId": "number-time", "resetTime": 1729285800 },
                { "bucketId": "missing-time" }
              ]
            }
          ]
        }
        """;

        var result = AntigravityQuotaParser.ParseQuotaSummary(json);
        Assert.Equal(5, result.Buckets.Count);

        Assert.Equal(DateTimeOffset.Parse("2026-10-18T22:30:00Z", CultureInfo.InvariantCulture), result.Buckets[0].ResetsAt);
        Assert.Null(result.Buckets[1].ResetsAt);
        Assert.Null(result.Buckets[2].ResetsAt);
        Assert.Null(result.Buckets[3].ResetsAt);
        Assert.Null(result.Buckets[4].ResetsAt);
    }

    [Fact]
    public void ParseGetUserStatusFallback_ValidConfigs_CalculatesUsedPercentAndResetTime()
    {
        const string json = """
        {
          "userStatus": {
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                {
                  "modelId": "gemini-pro-fallback",
                  "label": "Gemini 1.5 Pro Fallback",
                  "quotaInfo": {
                    "remainingFraction": 0.35,
                    "resetTime": "2026-10-18T21:00:00Z"
                  }
                },
                {
                  "modelId": "gemini-flash-fallback",
                  "label": "Gemini Flash Fallback",
                  "quotaInfo": {
                    "remaining": {
                      "remainingFraction": 0.0
                    }
                  }
                }
              ]
            }
          }
        }
        """;

        var result = AntigravityQuotaParser.ParseGetUserStatusFallback(json);

        Assert.Equal(2, result.Buckets.Count);

        var b0 = result.Buckets[0];
        Assert.Equal("gemini-pro-fallback", b0.Id);
        Assert.Equal("Gemini 1.5 Pro Fallback", b0.Name);
        Assert.Equal(65.0f, b0.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T21:00:00Z", CultureInfo.InvariantCulture), b0.ResetsAt);

        var b1 = result.Buckets[1];
        Assert.Equal("gemini-flash-fallback", b1.Id);
        Assert.Equal("Gemini Flash Fallback", b1.Name);
        Assert.Equal(100.0f, b1.UsedPercent);
        Assert.Null(b1.ResetsAt);
    }

    [Fact]
    public void ParseGetUserStatusFallback_MalformedTypesAndSiblings_HandledGracefully()
    {
        const string json = """
        {
          "userStatus": {
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                null,
                123,
                "bad config",
                {},
                { "modelId": "no-quota" },
                {
                  "modelId": "valid-fallback",
                  "label": "Valid Fallback Model",
                  "quotaInfo": {
                    "remainingFraction": 0.75
                  }
                }
              ]
            }
          }
        }
        """;

        var result = AntigravityQuotaParser.ParseGetUserStatusFallback(json);

        Assert.Equal(2, result.Buckets.Count);

        var noQuota = result.Buckets[0];
        Assert.Equal("no-quota", noQuota.Id);
        Assert.Null(noQuota.UsedPercent);

        var valid = result.Buckets[1];
        Assert.Equal("valid-fallback", valid.Id);
        Assert.Equal(25.0f, valid.UsedPercent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("\"hello\"")]
    [InlineData("{ invalid json }")]
    [InlineData("{\"userStatus\": null}")]
    [InlineData("{\"userStatus\": 123}")]
    [InlineData("{\"userStatus\": []}")]
    public void ParseGetUserStatusFallback_MalformedRoots_NoExceptions(string malformed)
    {
        var result = AntigravityQuotaParser.ParseGetUserStatusFallback(malformed);
        Assert.Empty(result.Buckets);
        Assert.Null(result.SummaryDescription);
    }
}
