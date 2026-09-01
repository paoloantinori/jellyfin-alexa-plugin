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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Background service that maintains an in-memory n-gram index of all Audio items.
/// Loads at startup and refreshes when songs are added/removed from the library.
/// Lifecycle (debounce, failed-load retry, readiness, dispose ordering) lives in
/// <see cref="DebouncedLibraryIndexService"/> (JF-419.3 extraction: the song index
/// gains the JF-419.1 failed-load self-recovery and the dispose-race fix for free).
/// </summary>
public class SongNgramIndexService : DebouncedLibraryIndexService, ISongNgramIndex
{
    private volatile Dictionary<string, List<BaseItem>> _bigramIndex = new();
    private volatile Dictionary<string, List<BaseItem>> _singleTokenIndex = new();
    private volatile Dictionary<string, List<BaseItem>> _phoneticTokenIndex = new();
    private volatile Dictionary<Guid, Guid> _songTopParentMap = new();
    private volatile List<BaseItem> _allEntries = [];

    /// <inheritdoc />
    public int SongCount => _allEntries.Count;

    /// <inheritdoc />
    public int NgramCount => _bigramIndex.Count;

    /// <inheritdoc />
    protected override string IndexName => "song n-gram";

    /// <inheritdoc />
    protected override bool ShouldRefreshOn(ItemChangeEventArgs eventArgs) => eventArgs.Item is Audio;

    /// <summary>
    /// Initializes a new instance of the <see cref="SongNgramIndexService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager for the load query and change events.</param>
    /// <param name="logger">Logger instance.</param>
    public SongNgramIndexService(ILibraryManager libraryManager, ILogger<SongNgramIndexService> logger)
        : base(libraryManager, logger)
    {
    }

    /// <summary>
    /// Adds a song to a string-keyed index under the given key.
    /// Shared by bigram, single-token, and phonetic index building.
    /// </summary>
    private static void AddToIndex(Dictionary<string, List<BaseItem>> index, string key, BaseItem song)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<BaseItem>();
            index[key] = list;
        }

        list.Add(song);
    }

    /// <inheritdoc />
    public List<(BaseItem Item, double Score)> SearchPhonetic(string[] keywordTokens, string locale, Guid[]? topParentIds = null)
    {
        // Layer-2 choke point (see IndexWarmingGate): a present-but-cold index throws
        // so no caller can silently treat warming as "no results" and run the cold DB
        // path. A DISABLED index degrades instead: empty results, DB fallback.
        IndexWarmingGate.EnsureReady(this);

        if (!IsReady || keywordTokens.Length == 0)
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
                foreach (var song in entries)
                {
                    candidateIds.Add(song.Id);
                }
            }

            if (!string.IsNullOrEmpty(alternate) && phoneticIdx.TryGetValue(alternate, out var altEntries))
            {
                foreach (var song in altEntries)
                {
                    candidateIds.Add(song.Id);
                }
            }
        }

        if (candidateIds.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var candidates = allSongs.Where(s => candidateIds.Contains(s.Id)).ToList();

        // Filter by library access
        if (topParentIds != null && topParentIds.Length > 0)
        {
            candidates = candidates.Where(s =>
                topParentMap.TryGetValue(s.Id, out var parentId) &&
                Array.IndexOf(topParentIds, parentId) >= 0).ToList();
        }

        if (candidates.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        return KeywordMatcher.ScorePhonetic(candidates, keywordTokens, locale);
    }

    /// <inheritdoc />
    public List<(BaseItem Item, double Score)> Search(string[] keywordTokens, string locale, Guid[]? topParentIds = null)
    {
        // Layer-2 choke point: see SearchPhonetic
        IndexWarmingGate.EnsureReady(this);

        if (!IsReady || keywordTokens.Length == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var bigramIdx = _bigramIndex;
        var singleIdx = _singleTokenIndex;
        var topParentMap = _songTopParentMap;
        var allSongs = _allEntries;

        // Collect candidate songs via bigram lookup or single-token scan
        HashSet<Guid> candidateIds;
        List<BaseItem> candidates;

        if (keywordTokens.Length >= 2)
        {
            // Generate bigrams from keyword tokens and look up in index
            candidateIds = new HashSet<Guid>();
            for (int i = 0; i < keywordTokens.Length - 1; i++)
            {
                string bigram = keywordTokens[i] + " " + keywordTokens[i + 1];
                if (bigramIdx.TryGetValue(bigram, out var entries))
                {
                    foreach (var song in entries)
                    {
                        candidateIds.Add(song.Id);
                    }
                }
            }

            if (candidateIds.Count == 0)
            {
                // No bigram hits; fall back to single-token scan
                return SearchBySingleTokens(keywordTokens, locale, topParentIds, singleIdx, topParentMap, allSongs);
            }

            candidates = allSongs.Where(s => candidateIds.Contains(s.Id)).ToList();
        }
        else
        {
            // Single keyword: scan single-token index
            return SearchBySingleTokens(keywordTokens, locale, topParentIds, singleIdx, topParentMap, allSongs);
        }

        // Filter by library access
        if (topParentIds != null && topParentIds.Length > 0)
        {
            candidates = candidates.Where(s =>
                topParentMap.TryGetValue(s.Id, out var parentId) &&
                Array.IndexOf(topParentIds, parentId) >= 0).ToList();
        }

        if (candidates.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        // Score candidates with KeywordMatcher
        return KeywordMatcher.Score(candidates, keywordTokens, locale);
    }

    /// <summary>
    /// Fallback search when bigram lookup yields no results or only a single keyword is provided.
    /// Scans the single-token index for any keyword match, then scores with KeywordMatcher.
    /// </summary>
    private static List<(BaseItem Item, double Score)> SearchBySingleTokens(
        string[] keywordTokens,
        string locale,
        Guid[]? topParentIds,
        Dictionary<string, List<BaseItem>> singleIdx,
        Dictionary<Guid, Guid> topParentMap,
        List<BaseItem> allSongs)
    {
        var candidateIds = new HashSet<Guid>();
        foreach (string token in keywordTokens)
        {
            if (singleIdx.TryGetValue(token, out var entries))
            {
                foreach (var song in entries)
                {
                    candidateIds.Add(song.Id);
                }
            }
        }

        if (candidateIds.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        var candidates = allSongs.Where(s => candidateIds.Contains(s.Id)).ToList();

        // Filter by library access
        if (topParentIds != null && topParentIds.Length > 0)
        {
            candidates = candidates.Where(s =>
                topParentMap.TryGetValue(s.Id, out var parentId) &&
                Array.IndexOf(topParentIds, parentId) >= 0).ToList();
        }

        if (candidates.Count == 0)
        {
            return new List<(BaseItem, double)>();
        }

        return KeywordMatcher.Score(candidates, keywordTokens, locale);
    }

    /// <inheritdoc />
    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<BaseItem> songs = await Task.Run(
            () => LibraryManager.GetItemList(query), cancellationToken).ConfigureAwait(false);

        // Build the token indexes (bigram, single-token, phonetic) and the
        // per-user library filter map
        var entries = new List<BaseItem>(songs.Count);
        var bigramIndex = new Dictionary<string, List<BaseItem>>(songs.Count);
        var singleTokenIndex = new Dictionary<string, List<BaseItem>>(songs.Count * 3);
        var phoneticTokenIndex = new Dictionary<string, List<BaseItem>>(songs.Count * 3);
        var topParentMap = new Dictionary<Guid, Guid>(songs.Count);

        foreach (var song in songs)
        {
            string[] tokens = KeywordMatcher.Tokenize(song.Name, "en-US");

            entries.Add(song);
            topParentMap[song.Id] = ResolveTopParentId(song);

            // Index bigrams (consecutive token pairs)
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                string bigram = tokens[i] + " " + tokens[i + 1];
                AddToIndex(bigramIndex, bigram, song);
            }

            // Index individual tokens for single-keyword fallback
            foreach (string token in tokens)
            {
                AddToIndex(singleTokenIndex, token, song);

                // Index phonetic codes for misspelling tolerance
                var (primary, alternate) = DoubleMetaphone.Encode(token);
                if (!string.IsNullOrEmpty(primary))
                {
                    AddToIndex(phoneticTokenIndex, primary, song);
                }

                if (!string.IsNullOrEmpty(alternate) && !string.Equals(alternate, primary, StringComparison.OrdinalIgnoreCase))
                {
                    AddToIndex(phoneticTokenIndex, alternate, song);
                }
            }
        }

        _bigramIndex = bigramIndex;
        _singleTokenIndex = singleTokenIndex;
        _phoneticTokenIndex = phoneticTokenIndex;
        _songTopParentMap = topParentMap;
        _allEntries = entries;

        Logger.LogInformation("Song n-gram index {Action}: {SongCount} songs, {BigramCount} bigrams, {TokenCount} unique tokens, {PhoneticCount} phonetic codes",
            songs.Count > 0 ? "loaded" : "initialized (empty library)",
            songs.Count,
            bigramIndex.Count,
            singleTokenIndex.Count,
            phoneticTokenIndex.Count);
    }
}
