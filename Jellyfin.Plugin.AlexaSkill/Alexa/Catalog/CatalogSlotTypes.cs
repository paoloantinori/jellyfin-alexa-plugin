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
    /// KNOWN MISMATCH (tracked in JF-332, facts corrected 2026-08-29): Album is
    /// uploaded to "AMAZON.Album", a type no locale model declares. The album slot
    /// type is NOT uniform across locales: ONLY it-IT declares "AlbumName"
    /// (catalog-backed, JF-96.2); the other 16 locales have declared
    /// AMAZON.MusicRecording since before 2026-07 (verified at the 2026-07-03
    /// commit). Consequences: (a) dynamic album values are inert everywhere
    /// (AMAZON.Album is declared nowhere); (b) a single "point Album at AlbumName"
    /// fix would only work for it-IT - in the other 16 locales it would recreate
    /// the same inert-type failure on a different name, and conversely the static
    /// catalog upload to "AlbumName" (CatalogSlotTypeNames) only reaches a
    /// declared type in it-IT. The JF-332 resolution needs a per-locale decision
    /// (per-locale catalog type names, or harmonizing the model slot type); do
    /// NOT "restore" a uniform AlbumName assumption from older comments.
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
        [CatalogType.Album] = "AlbumName",
        // JF-493: unlike AlbumName (it-IT only), SeriesName is declared by ALL 17
        // locale models as a static seed list, so the catalog injection REPLACES
        // the static type everywhere and no slot re-typing is needed.
        [CatalogType.Series] = "SeriesName"
    };
}
