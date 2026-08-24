using System;
using ReforgerScenarioRotation.BackgroundServices;
using Xunit;

namespace ReforgerScenarioRotation.Tests;

public class HungLogEvaluatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_log_window_is_inconclusive_not_hung()
    {
        // This is the EU-1 spam bug: docker `since` returned nothing even while the
        // server was up, and empty was treated as hung + re-alerted every 10 minutes.
        var result = HungLogEvaluator.Evaluate("", "", Now, Timeout);

        Assert.Equal(HungCheckResult.Inconclusive, result);
        Assert.False(HungLogEvaluator.ShouldSendHungAlert(wasHung: false, result));
        Assert.False(HungLogEvaluator.IsNowHung(wasHung: false, result));
    }

    [Fact]
    public void Whitespace_only_logs_are_inconclusive()
    {
        var result = HungLogEvaluator.Evaluate("   \n", "\t", Now, Timeout);
        Assert.Equal(HungCheckResult.Inconclusive, result);
    }

    [Fact]
    public void Recent_stdout_timestamp_is_healthy()
    {
        var logs = "2026-08-24T15:58:00.123456789Z BACKEND    (E): player connected\n";
        var result = HungLogEvaluator.Evaluate(logs, "", Now, Timeout);
        Assert.Equal(HungCheckResult.Healthy, result);
    }

    [Fact]
    public void Recent_stderr_only_timestamp_is_healthy()
    {
        var logs = "2026-08-24T15:58:30Z SCRIPT     (E): tick\n";
        var result = HungLogEvaluator.Evaluate("", logs, Now, Timeout);
        Assert.Equal(HungCheckResult.Healthy, result);
    }

    [Fact]
    public void Stale_last_log_is_hung()
    {
        var logs = "2026-08-24T15:50:00Z last line before freeze\n";
        var result = HungLogEvaluator.Evaluate(logs, "", Now, Timeout);
        Assert.Equal(HungCheckResult.Hung, result);
        Assert.True(HungLogEvaluator.ShouldSendHungAlert(wasHung: false, result));
    }

    [Fact]
    public void Hung_alert_is_only_sent_on_transition()
    {
        var result = HungCheckResult.Hung;
        Assert.True(HungLogEvaluator.ShouldSendHungAlert(wasHung: false, result));
        Assert.False(HungLogEvaluator.ShouldSendHungAlert(wasHung: true, result));
    }

    [Fact]
    public void Recovery_alert_is_only_sent_when_logs_resume()
    {
        Assert.True(HungLogEvaluator.ShouldSendRecoveryAlert(wasHung: true, HungCheckResult.Healthy));
        Assert.False(HungLogEvaluator.ShouldSendRecoveryAlert(wasHung: true, HungCheckResult.Inconclusive));
        Assert.False(HungLogEvaluator.ShouldSendRecoveryAlert(wasHung: false, HungCheckResult.Healthy));
    }

    [Fact]
    public void Inconclusive_keeps_previous_hung_state()
    {
        Assert.True(HungLogEvaluator.IsNowHung(wasHung: true, HungCheckResult.Inconclusive));
        Assert.False(HungLogEvaluator.IsNowHung(wasHung: false, HungCheckResult.Inconclusive));
    }

    [Fact]
    public void Koth1_maps_to_eu1_instead_of_defaulting_unknown_names()
    {
        Assert.Equal("EU-1", HungLogEvaluator.ToDisplayName("koth1-koth-reforged-1-1"));
        Assert.Equal("EU-2", HungLogEvaluator.ToDisplayName("koth2-koth-reforged-2-1"));
        Assert.Equal("sidecar", HungLogEvaluator.ToDisplayName("sidecar"));
    }

    [Fact]
    public void Container_match_requires_exact_name_not_substring()
    {
        var names = new[] { "/koth1-koth-reforged-1-1" };
        Assert.True(HungLogEvaluator.MatchesContainerName(names, "koth1-koth-reforged-1-1"));
        Assert.False(HungLogEvaluator.MatchesContainerName(names, "koth1"));
        Assert.False(HungLogEvaluator.MatchesContainerName(names, "arma-koth-reforged-1-1"));
    }
}
