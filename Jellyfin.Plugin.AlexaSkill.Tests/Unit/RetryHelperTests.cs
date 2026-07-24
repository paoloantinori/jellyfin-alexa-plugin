using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Microsoft.Extensions.Logging;
using Refit;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class RetryHelperTests
{
    private readonly ILogger _logger;

    public RetryHelperTests()
    {
        _logger = LoggerFactory.Create(b => { }).CreateLogger<RetryHelperTests>();
    }

    // --- Synchronous overload (Func<T>) tests ---

    [Fact]
    public async Task Sync_SuccessOnFirstAttempt_ReturnsResult()
    {
        int calls = 0;
        string result = await RetryHelper.ExecuteWithRetryAsync(
            (Func<string>)(() =>
            {
                calls++;
                return "ok";
            }),
            _logger,
            "TestOp");

        Assert.Equal("ok", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Sync_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        int calls = 0;
        string result = await RetryHelper.ExecuteWithRetryAsync(
            (Func<string>)(() =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new HttpRequestException("transient");
                }

                return "ok";
            }),
            _logger,
            "TestOp",
            maxRetries: 3,
            initialDelayMs: 1);

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Sync_AllRetriesExhausted_Throws()
    {
        int calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<int>)(() =>
                {
                    calls++;
                    throw new HttpRequestException("persistent");
                }),
                _logger,
                "TestOp",
                maxRetries: 2,
                initialDelayMs: 1));

        // 1 initial + 2 retries = 3 total
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Sync_NonTransientException_DoesNotRetry()
    {
        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<int>)(() =>
                {
                    calls++;
                    throw new InvalidOperationException("not transient");
                }),
                _logger,
                "TestOp",
                maxRetries: 3,
                initialDelayMs: 1));

        Assert.Equal(1, calls);
    }

    // --- Async overload (Func<Task<T>>) tests ---

    [Fact]
    public async Task Async_SuccessOnFirstAttempt_ReturnsResult()
    {
        int calls = 0;
        string result = await RetryHelper.ExecuteWithRetryAsync(
            () =>
            {
                calls++;
                return Task.FromResult("ok");
            },
            _logger,
            "TestOp");

        Assert.Equal("ok", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Async_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        int calls = 0;
        string result = await RetryHelper.ExecuteWithRetryAsync(
            () =>
            {
                calls++;
                if (calls < 2)
                {
                    return Task.FromException<string>(new TimeoutException());
                }

                return Task.FromResult("ok");
            },
            _logger,
            "TestOp",
            maxRetries: 2,
            initialDelayMs: 1);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Async_AllRetriesExhausted_Throws()
    {
        int calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    calls++;
                    return Task.FromException<string>(new TimeoutException("persistent"));
                },
                _logger,
                "TestOp",
                maxRetries: 2,
                initialDelayMs: 1));

        Assert.Equal(3, calls);
    }

    // --- Cancellation tests ---

    [Fact]
    public async Task CancellationRequested_ThrowsImmediately()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<int>)(() => 1),
                _logger,
                "TestOp",
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task UserCancellation_DoesNotRetry()
    {
        int calls = 0;
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<int>)(() =>
                {
                    calls++;
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }),
                _logger,
                "TestOp",
                maxRetries: 3,
                initialDelayMs: 1,
                cancellationToken: cts.Token));

        // Should not retry — user cancelled
        Assert.Equal(1, calls);
    }

    // --- IsTransient tests ---

    [Fact]
    public void IsTransient_HttpRequestException_ReturnsTrue()
    {
        Assert.True(RetryHelper.IsTransient(new HttpRequestException()));
    }

    [Fact]
    public void IsTransient_TimeoutException_ReturnsTrue()
    {
        Assert.True(RetryHelper.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void IsTransient_TaskCanceledException_WithNoToken_ReturnsTrue()
    {
        // TaskCanceledException without a cancelled token = HTTP timeout
        Assert.True(RetryHelper.IsTransient(new TaskCanceledException()));
    }

    [Fact]
    public void IsTransient_OperationCanceledException_WithCancelledToken_ReturnsFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(RetryHelper.IsTransient(new OperationCanceledException(cts.Token), cts.Token));
    }

    [Fact]
    public void IsTransient_InvalidOperationException_ReturnsFalse()
    {
        Assert.False(RetryHelper.IsTransient(new InvalidOperationException()));
    }

    [Fact]
    public void IsTransient_ArgumentNullException_ReturnsFalse()
    {
        Assert.False(RetryHelper.IsTransient(new ArgumentNullException()));
    }

    // --- CalculateDelay jitter tests ---

    [Fact]
    public void CalculateDelay_IsWithinBoundedRange()
    {
        const int initialDelay = 500;
        const int attempt = 1; // base = 500 * 2 = 1000

        int min = initialDelay * (int)Math.Pow(2, attempt);       // 1000
        int max = min + initialDelay / 2;                          // 1250

        // Sample 100 times to exercise randomness
        for (int i = 0; i < 100; i++)
        {
            int delay = RetryHelper.CalculateDelay(initialDelay, attempt);
            Assert.InRange(delay, min, max);
        }
    }

    [Fact]
    public void CalculateDelay_JitterIsNonDeterministic()
    {
        const int initialDelay = 500;
        const int attempt = 0;

        var delays = new HashSet<int>();
        for (int i = 0; i < 50; i++)
        {
            delays.Add(RetryHelper.CalculateDelay(initialDelay, attempt));
        }

        // With jitter range 0-250, 50 samples should produce multiple distinct values
        Assert.True(delays.Count > 1, $"Expected multiple distinct delays but got {delays.Count}");
    }

    [Fact]
    public void CalculateDelay_ZeroAttempt_NoOverflow()
    {
        // attempt=0: base=initialDelay, jitter=0..initialDelay/2
        int delay = RetryHelper.CalculateDelay(500, 0);
        Assert.InRange(delay, 500, 750);
    }

    [Fact]
    public void CalculateDelay_SmallInitialDelay_NoNegativeJitter()
    {
        // Edge case: initialDelay=1, jitter should be 0 to avoid division issues
        int delay = RetryHelper.CalculateDelay(1, 0);
        Assert.Equal(1, delay);
    }

    // --- Timeout budget tests ---

    [Fact]
    public async Task Sync_BudgetExceeded_SkipsRetryAndThrows()
    {
        int calls = 0;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<string>)(() =>
                {
                    calls++;
                    throw new HttpRequestException("transient");
                }),
                _logger,
                "TestOp",
                maxRetries: 3,
                initialDelayMs: 100,
                timeoutMs: 1,
                minOperationMs: 1));

        // Only the initial attempt; retry skipped because delay (100+) > budget (1ms)
        Assert.Equal(1, calls);
        Assert.Equal("transient", ex.Message);
    }

    [Fact]
    public async Task Async_BudgetExceeded_SkipsRetryAndThrows()
    {
        int calls = 0;
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    calls++;
                    return Task.FromException<string>(new TimeoutException("transient"));
                },
                _logger,
                "TestOp",
                maxRetries: 3,
                initialDelayMs: 100,
                timeoutMs: 1,
                minOperationMs: 1));

        Assert.Equal(1, calls);
        Assert.Equal("transient", ex.Message);
    }

    [Fact]
    public async Task Sync_BudgetSufficient_RetriesNormally()
    {
        int calls = 0;
        string result = await RetryHelper.ExecuteWithRetryAsync(
            (Func<string>)(() =>
            {
                calls++;
                if (calls < 2)
                {
                    throw new HttpRequestException("transient");
                }

                return "ok";
            }),
            _logger,
            "TestOp",
            maxRetries: 3,
            initialDelayMs: 1,
            timeoutMs: 10000,
            minOperationMs: 100);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Async_BudgetSufficient_RetriesNormally()
    {
        int calls = 0;
        string result = await RetryHelper.ExecuteWithRetryAsync(
            () =>
            {
                calls++;
                if (calls < 2)
                {
                    return Task.FromException<string>(new HttpRequestException("transient"));
                }

                return Task.FromResult("ok");
            },
            _logger,
            "TestOp",
            maxRetries: 3,
            initialDelayMs: 1,
            timeoutMs: 10000,
            minOperationMs: 100);

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NoTimeoutSpecified_RetriesAsBefore()
    {
        // Without timeoutMs, behavior is unchanged — all retries exhausted
        int calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<int>)(() =>
                {
                    calls++;
                    throw new HttpRequestException("persistent");
                }),
                _logger,
                "TestOp",
                maxRetries: 2,
                initialDelayMs: 1));

        // 1 initial + 2 retries = 3 total
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task BudgetTightButSufficient_SingleRetryAllowed()
    {
        int calls = 0;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<string>)(() =>
                {
                    calls++;
                    throw new HttpRequestException("fail");
                }),
                _logger,
                "TestOp",
                maxRetries: 5,
                initialDelayMs: 1,
                timeoutMs: 600,
                minOperationMs: 100));

        // First attempt near-instant, delay=1+jitter, minOp=100 → ~101ms < 600ms → 1 retry allowed
        // After first retry (~1ms elapsed), next delay=2+jitter → ~102ms < 600ms → another retry
        // This continues until delay grows enough to exceed budget
        // With budget=600 and minOp=100, budget allows retries until delay > 500
        Assert.True(calls >= 2, $"Expected at least 2 calls, got {calls}");
    }

    // --- Refit ApiException (412/429) tests ---

    private static ApiException CreateApiException(HttpStatusCode statusCode, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, "https://example.com");
        var response = new HttpResponseMessage(statusCode);
        response.Content = new StringContent("test");
        return ApiException.Create(request, method, response, null!).Result;
    }

    [Fact]
    public void IsTransient_RefitApiException_412_ReturnsTrue()
    {
        var ex = CreateApiException(HttpStatusCode.PreconditionFailed, HttpMethod.Put);
        Assert.True(RetryHelper.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_RefitApiException_429_ReturnsTrue()
    {
        var ex = CreateApiException(HttpStatusCode.TooManyRequests, HttpMethod.Post);
        Assert.True(RetryHelper.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_RefitApiException_400_ReturnsFalse()
    {
        var ex = CreateApiException(HttpStatusCode.BadRequest, HttpMethod.Post);
        Assert.False(RetryHelper.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_RefitApiException_404_ReturnsFalse()
    {
        var ex = CreateApiException(HttpStatusCode.NotFound, HttpMethod.Get);
        Assert.False(RetryHelper.IsTransient(ex));
    }

    [Fact]
    public async Task Async_Refit412_RetriesAndSucceeds()
    {
        int calls = 0;
        string result = await RetryHelper.ExecuteWithRetryAsync(
            () =>
            {
                calls++;
                if (calls < 3)
                {
                    var ex = CreateApiException(HttpStatusCode.PreconditionFailed, HttpMethod.Put);
                    return Task.FromException<string>(ex);
                }

                return Task.FromResult("ok");
            },
            _logger,
            "TestOp",
            maxRetries: 3,
            initialDelayMs: 1);

        Assert.Equal("ok", result);
        Assert.Equal(3, calls);
    }

    // --- Timeout-budget invariant (JF-359) ---
    // Guards the JF-358 class of failure: a play-path DB query that threw repeatedly used to burn
    // the whole retry budget (~8-12s wall time) and exceed Alexa's ~8s response window, surfacing
    // to the user as INVALID_RESPONSE. Every play-path Jellyfin call goes through RetryAsync, which
    // sets timeoutMs = AlexaRequestTimeoutMs (6000). The invariant: when an operation throws
    // transiently on every attempt, the retry loop must STOP once the timeoutMs budget is exhausted
    // — never run past it. If someone weakens/removes IsBudgetExceeded, this test fails before a
    // live user sees a timeout. (E2E correctness tests do not cover latency; this unit test is the
    // deterministic guard for the budget mechanism, not a live-timing measurement.)

    [Fact]
    public async Task Sync_AlwaysTransient_StopsWithinTimeoutBudget()
    {
        const int budgetMs = 1500; // small budget keeps the test fast; the invariant is relative
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Operation always throws a transient exception (HttpRequestException is transient).
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryHelper.ExecuteWithRetryAsync(
                (Func<int>)(() => throw new HttpRequestException("always transient")),
                _logger,
                "TestOp",
                maxRetries: 100, // allow many retries so the BUDGET (not the retry cap) is the stop condition
                initialDelayMs: 1,
                minOperationMs: 1,
                timeoutMs: budgetMs));

        sw.Stop();
        // Must stop within the budget (plus generous slack for scheduler/jitter, but well under
        // unbounded). If the budget check were removed, 100 retries at growing backoff would run
        // far longer than this bound.
        Assert.True(sw.ElapsedMilliseconds < budgetMs + 1000,
            $"Retry loop ran {sw.ElapsedMilliseconds}ms, exceeding the {budgetMs}ms budget — the timeout-budget guard (IsBudgetExceeded) is not stopping retries.");
    }
}
