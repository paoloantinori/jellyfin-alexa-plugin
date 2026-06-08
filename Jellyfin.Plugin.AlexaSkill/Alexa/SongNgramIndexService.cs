#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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
            candidates = candidates.Where(e =>
                topParentMap.TryGetValue(e.Song.Id, out var parentId) &&
                Array.IndexOf(topParentIds, parentId) >= 0).ToList();
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
            candidates = candidates.Where(e =>
                topParentMap.TryGetValue(e.Song.Id, out var parentId) &&
                Array.IndexOf(topParentIds, parentId) >= 0).ToList();
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
                    if (!bigramIndex.TryGetValue(bigram, out var list))
                    {
                        list = new List<SongEntry>();
                        bigramIndex[bigram] = list;
                    }

                    list.Add(entry);
                }

                // Index individual tokens for single-keyword fallback
                foreach (string token in tokens)
                {
                    if (!singleTokenIndex.TryGetValue(token, out var list))
                    {
                        list = new List<SongEntry>();
                        singleTokenIndex[token] = list;
                    }

                    list.Add(entry);
                }
            }

            _bigramIndex = bigramIndex;
            _singleTokenIndex = singleTokenIndex;
            _songTopParentMap = topParentMap;
            _allEntries = entries;
            _isReady = true;

            _logger.LogInformation("Song n-gram index {Action}: {SongCount} songs, {BigramCount} bigrams, {TokenCount} unique tokens",
                songs.Count > 0 ? "loaded" : "initialized (empty library)",
                songs.Count,
                bigramIndex.Count,
                singleTokenIndex.Count);
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
    /// Resolves the top-level parent folder ID for a song by walking up the parent chain.
    /// Used for per-user library filtering without DB queries.
    /// </summary>
    private Guid ResolveTopParentId(BaseItem item)
    {
        var seen = new HashSet<Guid>();
        BaseItem? current = item;
        while (current != null && current.ParentId != Guid.Empty)
        {
            if (!seen.Add(current.Id))
            {
                break; // Cycle protection
            }

            var parent = _libraryManager.GetItemById(current.ParentId);
            if (parent == null)
            {
                break;
            }

            current = parent;
        }

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
