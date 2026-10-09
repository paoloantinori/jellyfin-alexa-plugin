using System;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Pins the PerfGuard failure-message contract (JF-851): the message must carry
/// the FIRST attempt's and the RETRY's elapsed times as two separate measurements
/// (the pre-JF-851 shape reused one stopwatch, printing the retry elapsed twice).
/// Timing-shaped tests use Thread.Sleep floors (a sleep guarantees AT LEAST its
/// duration) and assert only the floors, never ceilings or cross-attempt ordering,
/// because measured elapsed includes arbitrary machine stalls that say nothing
/// about PerfGuard. Worst-case added suite time is the ~200ms of sleeps.
/// </summary>
public class PerfGuardTests
{
    private const int BudgetMs = 10;

    [Fact]
    public void FailureMessage_PrintsFirstAttemptAndRetryAsDistinctValues()
    {
        var attempt = 0;
        var ex = Record.Exception(() => PerfGuard.UnderMs(
            () =>
            {
                var n = attempt++;
                // attempt 0 = warmup (unmeasured), 1 = first attempt, 2 = retry
                if (n == 1)
                {
                    Thread.Sleep(150);
                }
                else if (n == 2)
                {
                    Thread.Sleep(50);
                }
            },
            budgetMs: BudgetMs,
            label: "JF851 pin"));

        Assert.NotNull(ex);
        var msg = ex.Message;
        var m = Regex.Match(msg, @"took (\d+)ms on retry, first attempt (\d+)ms, expected < " + BudgetMs + @"ms");
        Assert.True(m.Success, $"Message shape drifted: {msg}");
        Assert.True(long.TryParse(m.Groups[1].Value, out var retryMs), msg);
        Assert.True(long.TryParse(m.Groups[2].Value, out var firstMs), msg);
        // The floors: each printed value reflects ITS OWN attempt (the bug was the
        // same number printed twice). No upper bounds and no first>retry ordering:
        // a machine stall can inflate either measurement legitimately.
        Assert.True(firstMs >= 150, $"first attempt floor not honored: {firstMs}ms");
        Assert.True(retryMs >= 50, $"retry floor not honored: {retryMs}ms");
        Assert.Contains("JF851 pin", msg);
    }

    [Fact]
    public void FastFirstAttempt_PassesWithoutRetry()
    {
        var calls = 0;
        PerfGuard.UnderMs(() => calls++, budgetMs: int.MaxValue, label: "fast");
        // warmup + one measured attempt; the int.MaxValue budget makes a stall-driven
        // retry unreachable, so 2 is the only shape a correct guard can produce.
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FirstAttemptBreachesButRetryPasses_Passes()
    {
        var attempt = 0;
        PerfGuard.UnderMs(
            () =>
            {
                var n = attempt++;
                if (n == 1)
                {
                    Thread.Sleep(60);
                }
            },
            budgetMs: BudgetMs,
            label: "noisy first");
        Assert.Equal(3, attempt); // attempts: warmup + first + retry
    }
}
