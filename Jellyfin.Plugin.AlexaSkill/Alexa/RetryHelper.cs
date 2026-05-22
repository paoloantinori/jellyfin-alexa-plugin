using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Refit;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Retry utility with exponential backoff for transient API failures.
/// Provides both async (for HTTP calls) and sync (for in-process API calls) retry paths.
/// </summary>
internal static class RetryHelper
{
    /// <summary>
    /// Default maximum number of retry attempts.
    /// </summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// Default initial delay in milliseconds before the first retry.
    /// </summary>
    public const int DefaultInitialDelayMs = 500;

    /// <summary>
    /// Default minimum estimated operation time in milliseconds.
    /// Used by timeout-aware retry to check if there's enough budget for another attempt.
    /// </summary>
    public const int DefaultMinOperationMs = 500;

    /// <summary>
    /// Check if the retry budget is exceeded.
    /// Returns true if budget is exceeded (caller should rethrow the caught exception).
    /// </summary>
    private static bool IsBudgetExceeded(Stopwatch stopwatch, int delayMs, int minOperationMs, int budgetMs, ILogger logger, string operationName)
    {
        if (stopwatch.ElapsedMilliseconds + delayMs + minOperationMs > budgetMs)
        {
            logger.LogWarning(
                "Skipping retry for {Operation}: timeout budget exceeded (elapsed={Elapsed}ms, nextDelay={Delay}ms, minOp={MinOp}ms, budget={Budget}ms)",
                operationName,
                stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                delayMs.ToString(CultureInfo.InvariantCulture),
                minOperationMs.ToString(CultureInfo.InvariantCulture),
                budgetMs.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Calculate exponential backoff delay with random jitter to prevent thundering herd.
    /// Formula: initialDelay * 2^attempt + random(0, initialDelay/2).
    /// </summary>
    /// <param name="initialDelayMs">Initial delay in milliseconds.</param>
    /// <param name="attempt">The current attempt number (zero-based).</param>
    /// <returns>The calculated delay in milliseconds.</returns>
    internal static int CalculateDelay(int initialDelayMs, int attempt)
    {
        int baseDelay = initialDelayMs * (int)Math.Pow(2, attempt);
        int jitter = initialDelayMs > 1 ? Random.Shared.Next(0, (initialDelayMs / 2) + 1) : 0;
        return baseDelay + jitter;
    }

    /// <summary>
    /// Execute a synchronous operation with retry logic and exponential backoff.
    /// Use for in-process API calls (LibraryManager, SessionManager) that may fail transiently.
    /// When <paramref name="timeoutMs"/> is set, skips retries that would exceed the budget.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The synchronous operation to execute.</param>
    /// <param name="logger">Logger for retry diagnostics.</param>
    /// <param name="operationName">Descriptive name for logging.</param>
    /// <param name="maxRetries">Maximum retry attempts (default 3).</param>
    /// <param name="initialDelayMs">Initial delay in ms before first retry (default 500).</param>
    /// <param name="timeoutMs">Optional total timeout budget in ms. Retries are skipped if elapsed + delay + minOperation would exceed this.</param>
    /// <param name="minOperationMs">Minimum estimated time for one operation attempt in ms (default 500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<T> operation,
        ILogger logger,
        string operationName,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultInitialDelayMs,
        int? timeoutMs = null,
        int minOperationMs = DefaultMinOperationMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Stopwatch? stopwatch = timeoutMs.HasValue ? Stopwatch.StartNew() : null;

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return operation();
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex, cancellationToken))
            {
                int delayMs = CalculateDelay(initialDelayMs, attempt);

                if (stopwatch != null && IsBudgetExceeded(stopwatch, delayMs, minOperationMs, timeoutMs!.Value, logger, operationName))
                {
                    throw;
                }

                logger.LogWarning(
                    "Retry {Attempt}/{MaxRetries} for {Operation} after {Delay}ms due to: {Error}",
                    (attempt + 1).ToString(CultureInfo.InvariantCulture),
                    maxRetries.ToString(CultureInfo.InvariantCulture),
                    operationName,
                    delayMs.ToString(CultureInfo.InvariantCulture),
                    ex.Message);

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Execute an async operation with retry logic and exponential backoff.
    /// Use for HTTP calls (LWA, progressive responses) that may fail transiently.
    /// When <paramref name="timeoutMs"/> is set, skips retries that would exceed the budget.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="logger">Logger for retry diagnostics.</param>
    /// <param name="operationName">Descriptive name for logging.</param>
    /// <param name="maxRetries">Maximum retry attempts (default 3).</param>
    /// <param name="initialDelayMs">Initial delay in ms before first retry (default 500).</param>
    /// <param name="timeoutMs">Optional total timeout budget in ms. Retries are skipped if elapsed + delay + minOperation would exceed this.</param>
    /// <param name="minOperationMs">Minimum estimated time for one operation attempt in ms (default 500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        string operationName,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultInitialDelayMs,
        int? timeoutMs = null,
        int minOperationMs = DefaultMinOperationMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Stopwatch? stopwatch = timeoutMs.HasValue ? Stopwatch.StartNew() : null;

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex, cancellationToken))
            {
                int delayMs = CalculateDelay(initialDelayMs, attempt);

                if (stopwatch != null && IsBudgetExceeded(stopwatch, delayMs, minOperationMs, timeoutMs!.Value, logger, operationName))
                {
                    throw;
                }

                logger.LogWarning(
                    "Retry {Attempt}/{MaxRetries} for {Operation} after {Delay}ms due to: {Error}",
                    (attempt + 1).ToString(CultureInfo.InvariantCulture),
                    maxRetries.ToString(CultureInfo.InvariantCulture),
                    operationName,
                    delayMs.ToString(CultureInfo.InvariantCulture),
                    ex.Message);

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Determine if an exception represents a transient failure worth retrying.
    /// Excludes user-initiated cancellation (token triggered by caller).
    /// </summary>
    /// <param name="ex">The exception to evaluate.</param>
    /// <param name="cancellationToken">The cancellation token to distinguish user cancellation from HTTP timeout.</param>
    /// <returns>True if the exception is transient and the operation should be retried.</returns>
    internal static bool IsTransient(Exception ex, CancellationToken cancellationToken = default)
    {
        if (ex is OperationCanceledException oce)
        {
            // If the caller's token triggered cancellation, do not retry
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            // TaskCanceledException without a cancelled token = HTTP timeout, worth retrying
            return oce.CancellationToken == CancellationToken.None || !oce.CancellationToken.IsCancellationRequested;
        }

        return ex is HttpRequestException
            || ex is TimeoutException
            || (ex is ApiException apiEx
                && (apiEx.StatusCode == HttpStatusCode.PreconditionFailed
                    || apiEx.StatusCode == HttpStatusCode.TooManyRequests));
    }
}
