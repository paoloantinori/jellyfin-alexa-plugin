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
}
