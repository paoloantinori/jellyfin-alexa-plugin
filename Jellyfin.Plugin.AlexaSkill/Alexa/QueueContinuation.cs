using System;
using Jellyfin.Data.Enums;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Continuation state for progressive queue building.
/// Stores enough information to fetch the next batch of items on demand,
/// enabling fast time-to-first-audio by only fetching an initial page.
/// </summary>
public class QueueContinuation
{
    /// <summary>
    /// Gets the source type for the continuation query.
    /// </summary>
    public string SourceType { get; init; } = string.Empty;

    /// <summary>
    /// Gets the parent ID (album ID for album tracks, playlist ID for playlist items).
    /// </summary>
    public Guid? ParentId { get; init; }

    /// <summary>
    /// Gets the artist ID (for artist songs queries).
    /// </summary>
    public Guid? ArtistId { get; init; }

    /// <summary>
    /// Gets the playlist ID (for playlist item queries).
    /// </summary>
    public Guid? PlaylistId { get; init; }

    /// <summary>
    /// Gets or sets the next offset to fetch from the result set.
    /// </summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the total number of items available in the full result set.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Gets or sets the number of items to fetch per continuation batch.
    /// </summary>
    public int BatchSize { get; set; } = ProgressiveQueueConstants.ContinuationBatchSize;

    /// <summary>
    /// Gets the Jellyfin user ID for the query.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// Gets the sort order used for the original query (to maintain consistency across batches).
    /// </summary>
    public (ItemSortBy SortBy, SortOrder Order)[]? SortOrder { get; init; }
}
