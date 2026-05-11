using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Diagnostics;

/// <summary>
/// Lightweight Jellyfin API connectivity check with time-based caching.
/// Calls GET /System/Info/Public to verify the server is reachable.
/// </summary>
public class JellyfinConnectivityChecker : IDisposable
{
    private readonly ILogger<JellyfinConnectivityChecker> _logger;
    private readonly TimeSpan _cacheDuration;
    private readonly TimeSpan _requestTimeout;

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private ConnectivityResult _cachedResult = new(false, "Not checked yet", 0, null);
    private string _lastServerAddress = string.Empty;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public JellyfinConnectivityChecker(ILogger<JellyfinConnectivityChecker> logger)
    {
        _logger = logger;
        _cacheDuration = TimeSpan.FromSeconds(30);
        _requestTimeout = TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Gets the current connectivity status, using a cached result if fresh (under 30s)
    /// and the server address hasn't changed.
    /// </summary>
    /// <returns>The connectivity check result.</returns>
    public async Task<ConnectivityResult> CheckAsync()
    {
        var config = Plugin.Instance!.Configuration;
        string serverAddress = config.ServerAddress ?? string.Empty;

        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            return new ConnectivityResult(false, "No server address configured", 0, null);
        }

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            bool cacheValid = _cachedAt != DateTimeOffset.MinValue
                && (DateTimeOffset.UtcNow - _cachedAt) < _cacheDuration
                && string.Equals(_lastServerAddress, serverAddress, StringComparison.Ordinal);

            if (cacheValid)
            {
                return _cachedResult;
            }

            var result = await PingServerAsync(serverAddress).ConfigureAwait(false);

            _cachedResult = result;
            _lastServerAddress = serverAddress;
            _cachedAt = DateTimeOffset.UtcNow;

            return result;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Invalidate the cached connectivity result, forcing a fresh check on the next <see cref="CheckAsync"/> call.
    /// </summary>
    public void InvalidateCache()
    {
        _semaphore.Wait();
        try
        {
            _cachedAt = DateTimeOffset.MinValue;
            _lastServerAddress = string.Empty;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<ConnectivityResult> PingServerAsync(string serverAddress)
    {
        try
        {
            // ServerAddress is normalized to end with '/' in PluginConfiguration
            string url = serverAddress + "System/Info/Public";
            using var cts = new CancellationTokenSource(_requestTimeout);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            HttpResponseMessage response = await Plugin.HttpClient
                .GetAsync(url, cts.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Jellyfin connectivity check succeeded in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
                return new ConnectivityResult(true, "OK", stopwatch.ElapsedMilliseconds, (int)response.StatusCode);
            }

            _logger.LogWarning("Jellyfin connectivity check failed: HTTP {StatusCode}", (int)response.StatusCode);
            return new ConnectivityResult(false, $"HTTP {(int)response.StatusCode} ({response.StatusCode})", stopwatch.ElapsedMilliseconds, (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Jellyfin connectivity check timed out after {TimeoutMs}ms", _requestTimeout.TotalMilliseconds);
            return new ConnectivityResult(false, $"Timeout after {_requestTimeout.TotalMilliseconds}ms", (long)_requestTimeout.TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin connectivity check failed");
            return new ConnectivityResult(false, ex.Message, 0, null);
        }
    }

    /// <summary>
    /// Releases the managed resources used by the <see cref="JellyfinConnectivityChecker"/>.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _semaphore.Dispose();
        }
    }

    /// <summary>
    /// Disposes the semaphore and other resources.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Result of a Jellyfin API connectivity check.
/// </summary>
/// <param name="IsReachable">Whether the Jellyfin server responded successfully.</param>
/// <param name="Message">Human-readable status message.</param>
/// <param name="ResponseTimeMs">Round-trip time in milliseconds.</param>
/// <param name="HttpStatusCode">HTTP status code if a response was received.</param>
public sealed record ConnectivityResult(bool IsReachable, string Message, long ResponseTimeMs, int? HttpStatusCode);
