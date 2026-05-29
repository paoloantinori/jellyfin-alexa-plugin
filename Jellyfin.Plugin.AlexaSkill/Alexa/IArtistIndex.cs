using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// In-memory index of MusicArtist items for fast artist search without DB queries.
/// </summary>
public interface IArtistIndex
{
    /// <summary>
    /// Get all indexed artists, optionally filtered by library top parent IDs.
    /// Returns an empty list if the index is not yet loaded.
    /// </summary>
    /// <param name="topParentIds">Physical folder IDs to filter by, or null for all artists.</param>
    /// <returns>Artists matching the filter.</returns>
    IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null);

    /// <summary>
    /// Whether the index has been loaded and is ready for queries.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Number of artists in the index.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Try to get the pre-computed Double Metaphone phonetic codes for an artist.
    /// Codes are computed once at index build time for zero per-request cost.
    /// </summary>
    /// <param name="artistId">The artist's item ID.</param>
    /// <param name="codes">Primary and alternate phonetic codes if found.</param>
    /// <returns>True if phonetic codes were found for this artist.</returns>
    bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes);
}
