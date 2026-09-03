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
    /// Whether the index gave up after repeated failed loads: treat as absent
    /// (degrade to database paths) rather than warming.
    /// </summary>
    bool IsDisabled { get; }

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

    /// <summary>
    /// JF-448 (review F2): capture the currently published snapshot as a PINNED read
    /// view. Every read through the returned index (<see cref="GetArtists"/>,
    /// <see cref="TryGetPhoneticCode"/>) resolves from the ONE snapshot published at
    /// capture time, so a concurrent refresh cannot mix the artist list of one publish
    /// with the phonetic codes of another inside a single search chain (a refresh
    /// landing between the two reads previously nulled the code lookup, skipped the
    /// JF-381 phonetic floor, and played the wrong artist for one request). Capture
    /// ONCE at chain entry and pass the view down; the call is idempotent (a view
    /// captures to itself), so a caller that pinned earlier adds no extra hop.
    /// </summary>
    /// <returns>A read view pinned to one published snapshot.</returns>
    IArtistIndex CaptureSnapshot();
}

/// <summary>
/// JF-448 (review F2): the ONE pin-or-degrade policy used by every artist search chain
/// entry. Pinning an already-pinned view is the view itself (idempotent); an
/// implementation whose capture returns null (a loose test mock, a third-party
/// implementation without pinning support) degrades to live reads, which is exactly the
/// pre-JF-448 behavior, rather than losing the in-memory path.
/// </summary>
internal static class ArtistIndexExtensions
{
    /// <summary>Pins the index's current snapshot for a whole search chain, or degrades to live reads.</summary>
    /// <param name="index">The index a chain is about to read; null stays null.</param>
    /// <returns>The pinned view, the index itself when it cannot pin, or null.</returns>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(index))]
    internal static IArtistIndex? Pin(this IArtistIndex? index) => index?.CaptureSnapshot() ?? index;
}
