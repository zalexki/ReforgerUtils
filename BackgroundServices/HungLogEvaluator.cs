using System;
using System.Text.RegularExpressions;

namespace ReforgerScenarioRotation.BackgroundServices;

public enum HungCheckResult
{
    Healthy,
    Hung,
    /// <summary>
    /// No parseable docker log timestamp. Do not alert or recover — the read failed or the
    /// container is not writing to stdout/stderr, which is not the same as a hung server.
    /// </summary>
    Inconclusive
}

/// <summary>
/// Pure hung-check rules. Docker log APIs are unreliable with <c>since</c> windows:
/// an empty window used to be treated as "server down" and re-alerted every 10 minutes.
/// </summary>
public static class HungLogEvaluator
{
    private static readonly Regex TimestampRegex = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DateTimeOffset? TryGetLastLogTimestamp(string stdout, string stderr)
    {
        DateTimeOffset? latest = null;
        Consider(stdout, ref latest);
        Consider(stderr, ref latest);
        return latest;
    }

    public static HungCheckResult Evaluate(string stdout, string stderr, DateTimeOffset now, TimeSpan timeout)
    {
        var last = TryGetLastLogTimestamp(stdout, stderr);
        if (last is null)
            return HungCheckResult.Inconclusive;

        return now - last.Value > timeout ? HungCheckResult.Hung : HungCheckResult.Healthy;
    }

    public static bool IsNowHung(bool wasHung, HungCheckResult result) => result switch
    {
        HungCheckResult.Hung => true,
        HungCheckResult.Healthy => false,
        _ => wasHung
    };

    public static bool ShouldSendHungAlert(bool wasHung, HungCheckResult result)
        => result == HungCheckResult.Hung && !wasHung;

    public static bool ShouldSendRecoveryAlert(bool wasHung, HungCheckResult result)
        => wasHung && result == HungCheckResult.Healthy;

    public static string ToDisplayName(string containerName) => containerName switch
    {
        "koth1-koth-reforged-1-1" => "EU-1",
        "koth2-koth-reforged-2-1" => "EU-2",
        "koth3-koth-reforged-3-1" => "EU-3",
        "arma-koth-reforged-1-1" => "NA-1",
        "arma2-koth-reforged-2-1" => "NA-2",
        "arma3-koth-reforged-3-1" => "NA-3",
        _ => containerName
    };

    public static bool MatchesContainerName(System.Collections.Generic.IEnumerable<string> dockerNames, string containerName)
    {
        foreach (var name in dockerNames)
        {
            var trimmed = name.TrimStart('/');
            if (trimmed == containerName)
                return true;
        }

        return false;
    }

    private static void Consider(string text, ref DateTimeOffset? latest)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (Match match in TimestampRegex.Matches(text))
        {
            if (!DateTimeOffset.TryParse(match.Value, out var ts))
                continue;

            if (latest is null || ts > latest)
                latest = ts;
        }
    }
}
