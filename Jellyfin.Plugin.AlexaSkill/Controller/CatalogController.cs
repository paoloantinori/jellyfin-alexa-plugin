#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Serves catalog JSON payloads for SMAPI to pull.
/// SMAPI creates catalog versions by fetching from these URLs.
/// </summary>
[ApiController]
[Route("alexaskill/catalog/")]
public class CatalogController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, CacheEntry> CatalogCache = new();
    private readonly ILogger<CatalogController> _logger;

    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);
    private const int MaxCacheSize = 20;

    public CatalogController(ILogger<CatalogController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Stores a catalog payload for later retrieval by SMAPI.
    /// Returns a cache key that can be embedded in the URL.
    /// </summary>
    /// <param name="payload">The catalog payload JSON string.</param>
    /// <returns>A cache key for the stored payload.</returns>
    public static string StorePayload(string payload)
    {
        string key = Guid.NewGuid().ToString("N");
        CatalogCache[key] = new CacheEntry(payload, DateTime.UtcNow);

        EvictExpired();

        return key;
    }

    /// <summary>
    /// Serves a catalog JSON payload by cache key.
    /// SMAPI fetches this URL when creating a catalog version.
    /// </summary>
    /// <param name="key">The cache key returned by StorePayload.</param>
    /// <returns>The catalog JSON.</returns>
    [HttpGet("{key}")]
    public ActionResult GetCatalog(string key)
    {
        if (string.IsNullOrEmpty(key) || !CatalogCache.TryGetValue(key, out CacheEntry? entry))
        {
            _logger.LogWarning("Catalog requested with unknown key: {Key}", key);
            return NotFound();
        }

        if (DateTime.UtcNow - entry.Created > EntryTtl)
        {
            CatalogCache.TryRemove(key, out _);
            _logger.LogWarning("Catalog requested with expired key: {Key}", key);
            return NotFound();
        }

        // Remove after successful fetch — SMAPI only reads each URL once
        CatalogCache.TryRemove(key, out _);
        return Content(entry.Payload, "application/json");
    }

    private static void EvictExpired()
    {
        DateTime cutoff = DateTime.UtcNow - EntryTtl;
        foreach (var kvp in CatalogCache)
        {
            if (kvp.Value.Created < cutoff)
            {
                CatalogCache.TryRemove(kvp.Key, out _);
            }
        }

        // If still over limit, evict oldest remaining entries
        if (CatalogCache.Count > MaxCacheSize)
        {
            foreach (var kvp in CatalogCache.OrderBy(k => k.Value.Created))
            {
                if (CatalogCache.Count <= MaxCacheSize)
                {
                    break;
                }

                CatalogCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class CacheEntry(string payload, DateTime created)
    {
        public string Payload { get; } = payload;
        public DateTime Created { get; } = created;
    }
}
