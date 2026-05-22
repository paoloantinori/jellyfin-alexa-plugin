#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

/// <summary>
/// Builds dynamic entity payloads from a user's Jellyfin library.
/// Artists are sourced from the in-memory <see cref="IArtistIndex"/> when available,
/// falling back to database queries. Albums are always queried from the database.
/// Injected into the Alexa NLU session via <c>Dialog.UpdateDynamicEntities</c>.
/// <para>
/// The full Build output is cached with a 2-minute TTL to avoid repeated DB queries
/// on every LaunchRequest/new session. The cache is invalidated automatically when
/// library items are added or removed.
/// </para>
/// </summary>
public class DynamicEntityBuilder : IDisposable
{
    private const int MaxTotalValueCount = 90;
    private const int DbQueryLimit = 55;
    private const int ArtistIndexLimit = 70;
    private const int LastPlayedCount = 5;
    private static readonly TimeSpan LastPlayedCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OutputCacheTtl = TimeSpan.FromMinutes(2);

    // Full-output cache keyed by (userId, locale, includeSeries, includeAudiobooks).
    // Supersedes the per-tier _lastPlayedCache on cache hit since Build() returns early.
    private static readonly ConcurrentDictionary<OutputCacheKey, (DynamicEntitiesDirective Directive, DateTime ExpiresAt)> _outputCache = new();

    // Expired entries evicted lazily on cache miss
    private readonly ConcurrentDictionary<Guid, (List<LastPlayedSlotValue> Values, DateTime ExpiresAt)> _lastPlayedCache = new();

    private readonly record struct OutputCacheKey(Guid UserId, string Locale, bool IncludeSeries, bool IncludeAudiobooks);

    private static readonly Dictionary<CatalogType, string> SlotTypeNames = CatalogSlotTypes.Names;

    private static readonly HashSet<string> TvIntents = new(StringComparer.Ordinal)
    {
        IntentNames.PlayEpisode,
        IntentNames.PlayVideo,
        IntentNames.ContinueWatching,
        IntentNames.SearchMedia,
        IntentNames.InProgressMediaList
    };

    private static readonly HashSet<string> BookIntents = new(StringComparer.Ordinal)
    {
        IntentNames.PlayBook,
        IntentNames.GoToChapter
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ILogger<DynamicEntityBuilder> _logger;
    private bool _disposed;

    public DynamicEntityBuilder(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<DynamicEntityBuilder> logger,
        IArtistIndex? artistIndex = null)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
        _artistIndex = artistIndex;

        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <summary>
    /// Builds a <c>Dialog.UpdateDynamicEntities</c> directive for a new session.
    /// Includes artists, albums, and last-played items.
    /// </summary>
    public virtual DynamicEntitiesDirective? Build(
        Guid jellyfinUserId,
        string locale,
        Guid[]? allowedLibraryIds,
        CancellationToken cancellationToken)
    {
        return Build(jellyfinUserId, locale, allowedLibraryIds, includeSeries: false, includeAudiobooks: false, cancellationToken);
    }

    /// <summary>
    /// Builds a <c>Dialog.UpdateDynamicEntities</c> directive from the user's library.
    /// Optionally includes series or audiobook entities based on conversation context.
    /// Always reserves 5 slots for last-played items.
    /// Results are cached for 2 minutes; cache is invalidated on library changes.
    /// </summary>
    public virtual DynamicEntitiesDirective? Build(
        Guid jellyfinUserId,
        string locale,
        Guid[]? allowedLibraryIds,
        bool includeSeries,
        bool includeAudiobooks,
        CancellationToken cancellationToken)
    {
        var cacheKey = new OutputCacheKey(jellyfinUserId, locale, includeSeries, includeAudiobooks);
        if (_outputCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            _logger.LogDebug("Dynamic entities cache hit for user {UserId}", jellyfinUserId);
            return cached.Directive;
        }

        EvictExpiredOutputCacheEntries();

        var user = _userManager.GetUserById(jellyfinUserId);
        if (user == null)
        {
            _logger.LogDebug("User {UserId} not found for dynamic entities", jellyfinUserId);
            return null;
        }

        var config = Plugin.Instance?.Configuration;
        bool musicEnabled = config?.MusicEnabled != false;
        bool videosEnabled = config?.VideosEnabled != false;
        bool booksEnabled = config?.BooksEnabled != false;

        int budget = MaxTotalValueCount;
        Guid[]? topParentIds = allowedLibraryIds != null
            ? LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager)
            : null;

        // Reserve budget for last-played items (5 slots)
        int baseBudget = budget - LastPlayedCount;

        List<DynamicSlotValue> artistValues = new();
        if (musicEnabled)
        {
            if (_artistIndex?.IsReady == true)
            {
                artistValues = BuildArtistValuesFromIndex(topParentIds, locale, ref baseBudget);
            }
            else
            {
                artistValues = BuildSlotValues(user, BaseItemKind.MusicArtist, CatalogType.Artist, locale, topParentIds, ref baseBudget);
            }
        }

        List<DynamicSlotValue> albumValues = new();
        if (musicEnabled)
        {
            albumValues = BuildSlotValues(user, BaseItemKind.MusicAlbum, CatalogType.Album, locale, topParentIds, ref baseBudget);
        }

        List<DynamicSlotValue> seriesValues = new();
        if (includeSeries && videosEnabled)
        {
            seriesValues = BuildSlotValues(user, BaseItemKind.Series, CatalogType.Series, locale, topParentIds, ref baseBudget);
        }

        List<DynamicSlotValue> audiobookValues = new();
        if (includeAudiobooks && booksEnabled)
        {
            audiobookValues = BuildSlotValues(user, BaseItemKind.AudioBook, CatalogType.Audiobook, locale, topParentIds, ref baseBudget);
        }

        // Sync budget: whatever base queries didn't use plus the reserved last-played slots
        budget = baseBudget + LastPlayedCount;
        var lastPlayedValues = BuildLastPlayedValues(user, locale, topParentIds, config, ref budget);

        if (artistValues.Count == 0 && albumValues.Count == 0 && seriesValues.Count == 0
            && audiobookValues.Count == 0 && lastPlayedValues.Count == 0)
        {
            _logger.LogDebug("No items found for dynamic entities");
            return null;
        }

        var directive = new DynamicEntitiesDirective();

        AddSlotType(directive, CatalogType.Artist, artistValues);
        AddSlotType(directive, CatalogType.Album, albumValues);
        AddSlotType(directive, CatalogType.Series, seriesValues);
        AddSlotType(directive, CatalogType.Audiobook, audiobookValues);

        // Merge last-played items into their respective slot types (deduped)
        DistributeLastPlayed(directive, lastPlayedValues);

        _logger.LogDebug(
            "Built dynamic entities: {Artists} artists ({ArtistSource}), {Albums} albums, {Series} series, {Audiobooks} audiobooks, {LastPlayed} last-played ({Used} of {Max} budget)",
            artistValues.Count,
            _artistIndex?.IsReady == true ? "index" : "db",
            albumValues.Count,
            seriesValues.Count,
            audiobookValues.Count,
            lastPlayedValues.Count,
            MaxTotalValueCount - budget,
            MaxTotalValueCount);

        _outputCache[cacheKey] = (directive, DateTime.UtcNow.Add(OutputCacheTtl));
        return directive;
    }

    /// <summary>
    /// Determines whether the given intent name suggests TV/series context.
    /// </summary>
    public static bool IsTvContext(string intentName) => TvIntents.Contains(intentName);

    /// <summary>
    /// Determines whether the given intent name suggests audiobook context.
    /// </summary>
    public static bool IsBookContext(string intentName) => BookIntents.Contains(intentName);

    /// <summary>
    /// Clears the full-output cache. Called automatically when library items are added or removed.
    /// Can also be called manually to force a refresh.
    /// </summary>
    public void InvalidateCache() => _outputCache.Clear();

    /// <summary>
    /// Evicts expired entries from the output cache. Called on cache miss to keep the dictionary bounded.
    /// </summary>
    private static void EvictExpiredOutputCacheEntries()
    {
        foreach (var kvp in _outputCache)
        {
            if (kvp.Value.ExpiresAt <= DateTime.UtcNow)
            {
                _outputCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _outputCache.Clear();
        _lastPlayedCache.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        _disposed = true;
    }

    private static DynamicSlotType GetOrAddSlotType(DynamicEntitiesDirective directive, string slotTypeName)
    {
        var existing = directive.Types.FirstOrDefault(t => t.Name == slotTypeName);
        if (existing != null)
        {
            return existing;
        }

        var newType = new DynamicSlotType { Name = slotTypeName, Values = new List<DynamicSlotValue>() };
        directive.Types.Add(newType);
        return newType;
    }

    private static void AddSlotType(DynamicEntitiesDirective directive, CatalogType type, List<DynamicSlotValue> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        GetOrAddSlotType(directive, SlotTypeNames[type]).Values.AddRange(values);
    }

    private static void DistributeLastPlayed(DynamicEntitiesDirective directive, List<LastPlayedSlotValue> lastPlayedValues)
    {
        foreach (var lpv in lastPlayedValues)
        {
            var slotValue = new DynamicSlotValue
            {
                Id = lpv.Id,
                Name = new DynamicSlotValueName { Value = lpv.DisplayName }
            };

            var slotType = GetOrAddSlotType(directive, lpv.SlotTypeName);
            if (!slotType.Values.Any(v => v.Id == slotValue.Id))
            {
                slotType.Values.Add(slotValue);
            }
        }
    }

    private List<LastPlayedSlotValue> BuildLastPlayedValues(
        Jellyfin.Database.Implementations.Entities.User user,
        string locale,
        Guid[]? topParentIds,
        PluginConfiguration? config,
        ref int budget)
    {
        if (_lastPlayedCache.TryGetValue(user.Id, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
        {
            budget -= Math.Min(cached.Values.Count, budget);
            return cached.Values;
        }

        // Evict expired entries on cache miss
        foreach (var kvp in _lastPlayedCache)
        {
            if (kvp.Value.ExpiresAt <= DateTime.UtcNow)
            {
                _lastPlayedCache.TryRemove(kvp.Key, out _);
            }
        }

        var itemTypes = new List<BaseItemKind>();
        if (config?.MusicEnabled != false)
        {
            itemTypes.Add(BaseItemKind.Audio);
        }

        if (config?.VideosEnabled != false)
        {
            itemTypes.Add(BaseItemKind.Movie);
            itemTypes.Add(BaseItemKind.Episode);
        }

        if (config?.BooksEnabled != false)
        {
            itemTypes.Add(BaseItemKind.AudioBook);
        }

        if (itemTypes.Count == 0)
        {
            return new List<LastPlayedSlotValue>();
        }

        var query = new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = itemTypes.ToArray(),
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 15,
            DtoOptions = new DtoOptions(true)
        };

        if (topParentIds != null)
        {
            query.TopParentIds = topParentIds;
        }

        IReadOnlyList<BaseItem> recentItems = _libraryManager.GetItemList(query) ?? Array.Empty<BaseItem>();
        var values = new List<LastPlayedSlotValue>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BaseItem item in recentItems)
        {
            if (values.Count >= LastPlayedCount || budget <= 0)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            // Deduplicate by name to avoid "Song X" appearing twice
            if (!seenNames.Add(item.Name))
            {
                continue;
            }

            var (slotTypeName, catalogType) = GetSlotTypeForItem(item);
            if (slotTypeName == null)
            {
                continue;
            }

            values.Add(new LastPlayedSlotValue
            {
                Id = CatalogValue.FormatId(catalogType, item.Id),
                DisplayName = item.Name,
                SlotTypeName = slotTypeName
            });

            budget--;
        }

        _lastPlayedCache[user.Id] = (values, DateTime.UtcNow.Add(LastPlayedCacheTtl));
        return values;
    }

    private (string? SlotTypeName, CatalogType CatalogType) GetSlotTypeForItem(BaseItem item)
    {
        // Audio tracks → map to AMAZON.Musician for better artist name recognition
        if (item is MediaBrowser.Controller.Entities.Audio.Audio audio
            && audio.Artists is { Count: > 0 })
        {
            return (SlotTypeNames[CatalogType.Artist], CatalogType.Artist);
        }

        // Episodes → SeriesName slot for series recognition
        if (item is MediaBrowser.Controller.Entities.TV.Episode)
        {
            return (SlotTypeNames[CatalogType.Series], CatalogType.Series);
        }

        // Movies → SeriesName slot (reuses the video slot for unified last-played matching)
        if (item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            return (SlotTypeNames[CatalogType.Series], CatalogType.Series);
        }

        // Audiobooks → AudiobookTitle slot (concrete type not in controller package, match by name)
        if (item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal))
        {
            return (SlotTypeNames[CatalogType.Audiobook], CatalogType.Audiobook);
        }

        // Plain audio without artist info
        if (item is MediaBrowser.Controller.Entities.Audio.Audio)
        {
            return (SlotTypeNames[CatalogType.Album], CatalogType.Album);
        }

        return (null, CatalogType.Artist);
    }

    private List<DynamicSlotValue> BuildArtistValuesFromIndex(
        Guid[]? topParentIds,
        string locale,
        ref int budget)
    {
        var allArtists = _artistIndex!.GetArtists(topParentIds);
        var values = new List<DynamicSlotValue>();

        foreach (BaseItem item in allArtists)
        {
            if (values.Count >= ArtistIndexLimit || !TryAddSlotValue(values, item, CatalogType.Artist, locale, ref budget))
            {
                break;
            }
        }

        return values;
    }

    private List<DynamicSlotValue> BuildSlotValues(
        Jellyfin.Database.Implementations.Entities.User user,
        BaseItemKind itemKind,
        CatalogType catalogType,
        string locale,
        Guid[]? topParentIds,
        ref int budget)
    {
        var query = new InternalItemsQuery
        {
            User = user,
            Recursive = true,
            IncludeItemTypes = new[] { itemKind },
            DtoOptions = new DtoOptions(true),
            Limit = DbQueryLimit,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
        };

        if (topParentIds != null)
        {
            query.TopParentIds = topParentIds;
        }

        IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(query);

        var values = new List<DynamicSlotValue>();

        foreach (BaseItem item in items)
        {
            if (!TryAddSlotValue(values, item, catalogType, locale, ref budget))
            {
                break;
            }
        }

        return values;
    }

    private bool TryAddSlotValue(
        List<DynamicSlotValue> values,
        BaseItem item,
        CatalogType catalogType,
        string locale,
        ref int budget)
    {
        if (string.IsNullOrWhiteSpace(item.Name) || budget <= 0)
        {
            return budget > 0;
        }

        var synonyms = PhoneticSynonymGenerator.GenerateSynonyms(item.Name, locale);
        int cost = 1 + synonyms.Count;

        if (cost > budget)
        {
            int fitSynonyms = Math.Max(0, budget - 1);
            synonyms = synonyms.Take(fitSynonyms).ToList();
            cost = 1 + synonyms.Count;
        }

        if (cost > budget)
        {
            return false;
        }

        values.Add(new DynamicSlotValue
        {
            Id = CatalogValue.FormatId(catalogType, item.Id),
            Name = new DynamicSlotValueName
            {
                Value = item.Name,
                Synonyms = synonyms.Count > 0 ? synonyms : null
            }
        });

        budget -= cost;
        return true;
    }

    private class LastPlayedSlotValue
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string SlotTypeName { get; set; } = string.Empty;
    }
}
