#nullable enable

using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// Maps catalog types to their Alexa slot type names.
/// </summary>
public static class CatalogSlotTypes
{
    /// <summary>
    /// Dynamic-entity runtime target slot types (session-scoped, delivered via
    /// Dialog.UpdateDynamicEntities in the response → effective from turn 2+).
    /// These MUST match the slot type the model actually declares for each entity,
    /// otherwise the runtime values land on an inert type nobody reads.
    /// </summary>
    /// <remarks>
    /// KNOWN MISMATCH (tracked in JF-332): Album → "AMAZON.Album", but no slot in
    /// any locale model uses AMAZON.Album — PlayAlbumIntent.album uses "AlbumName"
    /// (see <see cref="CatalogSlotTypeNames"/>). So dynamic album values are inert.
    /// The fix is to point Album at the model's real type ("AlbumName"), NOT to
    /// change the model slot to a built-in (see CatalogSlotTypeNames remarks).
    /// </remarks>
    public static readonly Dictionary<CatalogType, string> Names = new()
    {
        [CatalogType.Artist] = "AMAZON.Musician",
        [CatalogType.Album] = "AMAZON.Album", // JF-332: mismatched — model uses AlbumName
        [CatalogType.Series] = "SeriesName",
        [CatalogType.Audiobook] = "AudiobookTitle"
    };

    /// <summary>
    /// Catalog-backed slot types declared in the interaction model. Populated from
    /// the user's Jellyfin library by CatalogSyncTask (JF-96.2) with Italian
    /// phonetic synonyms for English names, for cross-language robustness.
    /// </summary>
    /// <remarks>
    /// DO NOT replace these with AMAZON built-in types (e.g. AMAZON.MusicRecording /
    /// AMAZON.Album) to "fix" one-shot routing for arbitrary library items. The
    /// custom type is deliberate: built-ins are English-biased and discard the
    /// phonetic-synonym matching that JF-96.2 built. One-shot routing for
    /// arbitrary items is provided by catalog sync populating these types, not by
    /// built-in free-text types. Swapping also blocks the catalog-sync path
    /// (sync writes to these names). Verified 2026-07-12: changing PlayAlbumIntent
    /// album slot AlbumName→AMAZON.MusicRecording made "jazz cafe" route one-shot
    /// but abandoned the architecture; reverted. See CLAUDE.md anti-pattern #10.
    /// </remarks>
    public static readonly Dictionary<CatalogType, string> CatalogSlotTypeNames = new()
    {
        [CatalogType.Artist] = "JellyfinArtist",
        [CatalogType.Album] = "AlbumName"
    };
}
