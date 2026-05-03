using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Microsoft.Extensions.Logging;
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
}
