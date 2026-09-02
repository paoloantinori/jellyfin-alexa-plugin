#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Background service that maintains an in-memory n-gram index of all Audio items.
/// Loads at startup and refreshes when songs are added/removed from the library.
/// Uses debounced refresh (5s window) to coalesce rapid library changes.
/// </summary>
public class SongNgramIndexService : ISongNgramIndex, IHostedService, IDisposable
{
    private const int RefreshDebounceSeconds = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SongNgramIndexService> _logger;
    private volatile Dictionary<string, List<SongEntry>> _bigramIndex = new();
    private volatile Dictionary<string, List<SongEntry>> _singleTokenIndex = new();
    private volatile Dictionary<string, List<SongEntry>> _phoneticTokenIndex = new();
    private volatile Dictionary<Guid, Guid> _songTopParentMap = new();
    private volatile List<SongEntry> _allEntries = [];
    private volatile bool _isReady;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _debounceLock = new();
    private Timer? _debounceTimer;
    private bool _disposed;

    public bool IsReady => _isReady;
    public int SongCount => _allEntries.Count;
    public int NgramCount => _bigramIndex.Count;

    public SongNgramIndexService(ILibraryManager libraryManager, ILogger<SongNgramIndexService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <inheritdoc />
    public List<(BaseItem Item, double Score)> SearchPhonetic(string[] keywordTokens, string locale, Guid[]? topParentIds = null)
    {
        if (!_isReady || keywordTokens.Length == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var phoneticIdx = _phoneticTokenIndex;
        var topParentMap = _songTopParentMap;
        var allSongs = _allEntries;

        // Encode each keyword token phonetically and collect candidate song IDs
        var candidateIds = new HashSet<Guid>();
        foreach (string token in keywordTokens)
        {
            var (primary, alternate) = DoubleMetaphone.Encode(token);

            if (!string.IsNullOrEmpty(primary) && phoneticIdx.TryGetValue(primary, out var entries))
            {
                foreach (var entry in entries)
                {
                    candidateIds.Add(entry.Song.Id);
                }
            }

            if (!string.IsNullOrEmpty(alternate) && phoneticIdx.TryGetValue(alternate, out var altEntries))
            {
                foreach (var entry in altEntries)
                {
                    candidateIds.Add(entry.Song.Id);
                }
            }
        }

        if (candidateIds.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var candidates = allSongs.Where(e => candidateIds.Contains(e.Song.Id)).ToList();

        // Filter by library access
        if (topParentIds != null && topParentIds.Length > 0)
        {
            var allowed = topParentIds.ToHashSet();
            candidates = candidates.Where(e =>
                topParentMap.TryGetValue(e.Song.Id, out var parentId) &&
                allowed.Contains(parentId)).ToList();
        }

        if (candidates.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var songs = candidates.Select(e => e.Song).ToList();
        return KeywordMatcher.ScorePhonetic(songs, keywordTokens, locale);
    }

    /// <summary>
    /// Adds a song entry to a string-keyed index under the given key.
    /// Shared by bigram, single-token, and phonetic index building.
    /// </summary>
    private static void AddToIndex(Dictionary<string, List<SongEntry>> index, string key, SongEntry entry)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<SongEntry>();
            index[key] = list;
        }

        list.Add(entry);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public List<(BaseItem Item, double Score)> Search(string[] keywordTokens, string locale, Guid[]? topParentIds = null)
    {
        if (!_isReady || keywordTokens.Length == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var bigramIdx = _bigramIndex;
        var singleIdx = _singleTokenIndex;
        var topParentMap = _songTopParentMap;
        var allSongs = _allEntries;

        // Collect candidate songs via bigram lookup or single-token scan
        HashSet<Guid> candidateIds;
        List<SongEntry> candidates;

        if (keywordTokens.Length >= 2)
        {
            // Generate bigrams from keyword tokens and look up in index
            candidateIds = new HashSet<Guid>();
            for (int i = 0; i < keywordTokens.Length - 1; i++)
            {
                string bigram = keywordTokens[i] + " " + keywordTokens[i + 1];
                if (bigramIdx.TryGetValue(bigram, out var entries))
                {
                    foreach (var entry in entries)
                    {
                        candidateIds.Add(entry.Song.Id);
                    }
                }
            }

            if (candidateIds.Count == 0)
            {
                // No bigram hits — fall back to single-token scan
                return SearchBySingleTokens(keywordTokens, locale, topParentIds, singleIdx, topParentMap, allSongs);
            }

            candidates = allSongs.Where(e => candidateIds.Contains(e.Song.Id)).ToList();
        }
        else
        {
            // Single keyword — scan single-token index
            return SearchBySingleTokens(keywordTokens, locale, topParentIds, singleIdx, topParentMap, allSongs);
        }

        // Filter by library access
        if (topParentIds != null && topParentIds.Length > 0)
        {
            var allowed = topParentIds.ToHashSet();
            candidates = candidates.Where(e =>
                topParentMap.TryGetValue(e.Song.Id, out var parentId) &&
                allowed.Contains(parentId)).ToList();
        }

        if (candidates.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        // Score candidates with KeywordMatcher
        var songs = candidates.Select(e => e.Song).ToList();
        return KeywordMatcher.Score(songs, keywordTokens, locale);
    }

    /// <summary>
    /// Fallback search when bigram lookup yields no results or only a single keyword is provided.
    /// Scans the single-token index for any keyword match, then scores with KeywordMatcher.
    /// </summary>
    private static List<(BaseItem Item, double Score)> SearchBySingleTokens(
        string[] keywordTokens,
        string locale,
        Guid[]? topParentIds,
        Dictionary<string, List<SongEntry>> singleIdx,
        Dictionary<Guid, Guid> topParentMap,
        List<SongEntry> allSongs)
    {
        var candidateIds = new HashSet<Guid>();
        foreach (string token in keywordTokens)
        {
            if (singleIdx.TryGetValue(token, out var entries))
            {
                foreach (var entry in entries)
                {
                    candidateIds.Add(entry.Song.Id);
                }
            }
        }

        if (candidateIds.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var candidates = allSongs.Where(e => candidateIds.Contains(e.Song.Id)).ToList();

        // Filter by library access
        if (topParentIds != null && topParentIds.Length > 0)
        {
            var allowed = topParentIds.ToHashSet();
            candidates = candidates.Where(e =>
                topParentMap.TryGetValue(e.Song.Id, out var parentId) &&
                allowed.Contains(parentId)).ToList();
        }

        if (candidates.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var songs = candidates.Select(e => e.Song).ToList();
        return KeywordMatcher.Score(songs, keywordTokens, locale);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                DtoOptions = new DtoOptions(true)
            };

            IReadOnlyList<BaseItem> songs = await Task.Run(
                () => _libraryManager.GetItemList(query), cancellationToken).ConfigureAwait(false);

            // Build song entries with tokenized titles
            var entries = new List<SongEntry>(songs.Count);
            var bigramIndex = new Dictionary<string, List<SongEntry>>(songs.Count);
            var singleTokenIndex = new Dictionary<string, List<SongEntry>>(songs.Count * 3);
            var phoneticTokenIndex = new Dictionary<string, List<SongEntry>>(songs.Count * 3);
            var topParentMap = new Dictionary<Guid, Guid>(songs.Count);

            foreach (var song in songs)
            {
                string[] tokens = KeywordMatcher.Tokenize(song.Name, "en-US");

                var entry = new SongEntry(song, tokens);
                entries.Add(entry);

                // Resolve top parent ID for library filtering
                topParentMap[song.Id] = ResolveTopParentId(song);

                // Index bigrams (consecutive token pairs)
                for (int i = 0; i < tokens.Length - 1; i++)
                {
                    string bigram = tokens[i] + " " + tokens[i + 1];
                    AddToIndex(bigramIndex, bigram, entry);
                }

                // Index individual tokens for single-keyword fallback
                foreach (string token in tokens)
                {
                    AddToIndex(singleTokenIndex, token, entry);

                    // Index phonetic codes for misspelling tolerance
                    var (primary, alternate) = DoubleMetaphone.Encode(token);
                    if (!string.IsNullOrEmpty(primary))
                    {
                        AddToIndex(phoneticTokenIndex, primary, entry);
                    }

                    if (!string.IsNullOrEmpty(alternate) && !string.Equals(alternate, primary, StringComparison.OrdinalIgnoreCase))
                    {
                        AddToIndex(phoneticTokenIndex, alternate, entry);
                    }
                }
            }

            _bigramIndex = bigramIndex;
            _singleTokenIndex = singleTokenIndex;
            _phoneticTokenIndex = phoneticTokenIndex;
            _songTopParentMap = topParentMap;
            _allEntries = entries;
            _isReady = true;

            _logger.LogInformation("Song n-gram index {Action}: {SongCount} songs, {BigramCount} bigrams, {TokenCount} unique tokens, {PhoneticCount} phonetic codes",
                songs.Count > 0 ? "loaded" : "initialized (empty library)",
                songs.Count,
                bigramIndex.Count,
                singleTokenIndex.Count,
                phoneticTokenIndex.Count);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load song n-gram index");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Resolves the library folder ID for an item by walking up the parent chain.
    /// Used for per-user library filtering without DB queries. The stop condition has
    /// FULL parity with Jellyfin's own <c>BaseItem.IsTopParent</c> boundary (all three
    /// edges: plugin folders and channels, live-tv views, and a parent that is the
    /// server-wide <see cref="AggregateFolder"/> root, whose ID cannot discriminate per
    /// library). This is the id space Jellyfin stores as the TopParentId column and the
    /// library filter resolves to, so the index maps and the filter agree for
    /// library-restricted users (JF-455). When the chain ends without a boundary node
    /// the LAST reached node's ID is returned: the top folder's ID for a chain that
    /// tops out at a parentless folder, or the item's own ID when it has no chain at
    /// all. Kept identical to ArtistIndexService's copy: the shared base class that
    /// single-sources this walk on main does not exist at this tag.
    /// </summary>
    /// <param name="item">The item to resolve.</param>
    /// <returns>The library folder ID (the last reached node's ID when no boundary is hit).</returns>
    private Guid ResolveTopParentId(BaseItem item)
    {
        var seen = new HashSet<Guid>();
        BaseItem? current = item;
        while (current != null)
        {
            BaseItem? parent = current.ParentId == Guid.Empty
                ? null
                : _libraryManager.GetItemById(current.ParentId);

            // IsTopParent parity (BaseItem.cs, v10.11.x): the node itself is a
            // boundary, or its parent is the server-wide aggregate root.
            if (current is BasePluginFolder
                || current is Channel
                || (current is IHasCollectionType view && view.CollectionType == CollectionType.livetv)
                || parent is AggregateFolder)
            {
                return current.Id;
            }

            if (parent == null || !seen.Add(current.Id))
            {
                break; // Chain end or cycle protection
            }

            current = parent;
        }

        // Chain ended without a boundary node (parentless top folder, stale parent
        // id, or cycle): return the LAST REACHED node's id. For a chain that
        // naturally tops out at a parentless folder this is that folder's id (the
        // library root in that shape); for an item with no chain at all this is
        // the item's own id.
        return current?.Id ?? item.Id;
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (_disposed || e.Item is not Audio)
        {
            return;
        }

        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                async _ =>
                {
                    try
                    {
                        await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Debounced song n-gram index refresh failed");
                    }
                },
                null,
                TimeSpan.FromSeconds(RefreshDebounceSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        _refreshLock.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Internal entry representing a song and its pre-computed title tokens.
    /// </summary>
    private sealed class SongEntry
    {
        public BaseItem Song { get; }
        public string[] TitleTokens { get; }

        public SongEntry(BaseItem song, string[] titleTokens)
        {
            Song = song;
            TitleTokens = titleTokens;
        }
    }
}
