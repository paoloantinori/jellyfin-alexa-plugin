using System;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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
    /// Execute a synchronous operation with retry logic and exponential backoff.
    /// Use for in-process API calls (LibraryManager, SessionManager) that may fail transiently.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The synchronous operation to execute.</param>
    /// <param name="logger">Logger for retry diagnostics.</param>
    /// <param name="operationName">Descriptive name for logging.</param>
    /// <param name="maxRetries">Maximum retry attempts (default 3).</param>
    /// <param name="initialDelayMs">Initial delay in ms before first retry (default 500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<T> operation,
        ILogger logger,
        string operationName,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultInitialDelayMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return operation();
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex, cancellationToken))
            {
                int delayMs = initialDelayMs * (int)Math.Pow(2, attempt);
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
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="logger">Logger for retry diagnostics.</param>
    /// <param name="operationName">Descriptive name for logging.</param>
    /// <param name="maxRetries">Maximum retry attempts (default 3).</param>
    /// <param name="initialDelayMs">Initial delay in ms before first retry (default 500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger logger,
        string operationName,
        int maxRetries = DefaultMaxRetries,
        int initialDelayMs = DefaultInitialDelayMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex, cancellationToken))
            {
                int delayMs = initialDelayMs * (int)Math.Pow(2, attempt);
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
            || ex is TimeoutException;
    }
}
