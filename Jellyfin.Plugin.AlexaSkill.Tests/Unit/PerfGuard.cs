using System;
using System.Diagnostics;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Noise-robust wrapper for the perf regression guards (JF-641): a bare wall-clock
/// assertion fails on cold-JIT or loaded-machine spikes that have nothing to do with
/// the code under test (live: 13ms vs a 10ms budget on a loaded first run, green on
/// rerun and idle). Contract: one WARMUP invocation (paid JIT/first-call cost outside
/// the measurement), then up to two measured attempts; a genuine regression (the
/// index lost its O(1)-ish shape) breaches EVERY attempt and still fails with the
/// observed budget in the message. Not an SLA: the intent is order-of-magnitude.
/// </summary>
internal static class PerfGuard
{
    public static void UnderMs(Action action, int budgetMs, string label)
    {
        action(); // warmup: JIT + first-call cost outside the measured window

        var first = Stopwatch.StartNew();
        action();
        first.Stop();
        if (first.ElapsedMilliseconds < budgetMs)
        {
            return;
        }

        // One retry on breach: a scheduler hiccup passes here; a real regression
        // breaches again and fails with both observations. Two stopwatches, so each
        // attempt's elapsed is independently measurable (JF-851): do NOT collapse
        // these back to one variable.
        var retry = Stopwatch.StartNew();
        action();
        retry.Stop();
        Assert.True(
            retry.ElapsedMilliseconds < budgetMs,
            $"{label} took {retry.ElapsedMilliseconds}ms on retry, first attempt {first.ElapsedMilliseconds}ms, expected < {budgetMs}ms on at least one of two attempts. A consistent breach means a real regression; a single-attempt breach was environment noise.");
    }
}
