#nullable enable
using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// In-memory n-gram index of song titles for fast partial-title lookup.
/// Uses bigrams (consecutive token pairs) to achieve O(1) candidate retrieval,
/// then ranks with KeywordMatcher.Score for final ordering.
/// </summary>
public interface ISongNgramIndex
{
    /// <summary>
    /// Whether the index has been loaded and is ready for queries.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Number of songs currently in the index.
    /// </summary>
    int SongCount { get; }

    /// <summary>
    /// Number of unique bigrams in the index.
    /// </summary>
    int NgramCount { get; }

    /// <summary>
    /// Search for songs matching the given keyword tokens using bigram lookup.
    /// Falls back to single-token scan when only one keyword is provided.
    /// Results are ranked by KeywordMatcher scoring.
    /// </summary>
    /// <param name="keywordTokens">Pre-tokenized user keywords.</param>
    /// <param name="locale">Locale string for tokenization and scoring.</param>
    /// <param name="topParentIds">Optional library folder IDs to filter by.</param>
    /// <returns>Ranked list of (Item, Score) tuples, sorted by score descending.</returns>
    List<(BaseItem Item, double Score)> Search(string[] keywordTokens, string locale, Guid[]? topParentIds = null);

    /// <summary>
    /// Search for songs using phonetic (Double Metaphone) matching on title tokens.
    /// Only intended as a fallback when <see cref="Search"/> returns no results.
    /// Encodes user keywords phonetically and matches against pre-computed phonetic
    /// token codes in the index. Uses relaxed keyword coverage (50%+) and applies
    /// a score penalty to rank phonetic matches below exact matches.
    /// </summary>
    /// <param name="keywordTokens">Pre-tokenized user keywords.</param>
    /// <param name="locale">Locale string for tokenization and scoring.</param>
    /// <param name="topParentIds">Optional library folder IDs to filter by.</param>
    /// <returns>Ranked list of (Item, Score) tuples with phonetic penalty applied.</returns>
    List<(BaseItem Item, double Score)> SearchPhonetic(string[] keywordTokens, string locale, Guid[]? topParentIds = null);
}
