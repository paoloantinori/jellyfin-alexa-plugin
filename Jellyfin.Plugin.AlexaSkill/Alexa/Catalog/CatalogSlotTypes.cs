#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

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
    /// Artist is the exception: since JF-415 its target is locale-dependent
    /// (<see cref="ResolveMusicianSlotType"/>) because only the locales in
    /// <see cref="CatalogBackedMusicianLocales"/> declare JellyfinArtist;
    /// the entry here is the built-in fallback of the other locales and the
    /// "type being replaced" for the catalog injection.
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
        [CatalogType.Artist] = "AMAZON.Musician", // locale fallback; see ResolveMusicianSlotType (JF-415)
        [CatalogType.Album] = "AMAZON.Album", // JF-332: mismatched (model uses AlbumName)
        [CatalogType.Series] = "SeriesName",
        [CatalogType.Audiobook] = "AudiobookTitle"
    };

    /// <summary>
    /// Locales whose committed interaction models declare the musician slot as the
    /// catalog-backed <c>JellyfinArtist</c> type (JF-415): the 5 en-* locales, where
    /// the AMAZON.Musician built-in replaces the spoken name with a knowledge-graph
    /// canonical (queen became "Paula Abdul"; research 2026-08-30), and it-IT, where
    /// the catalog anchor is what lets in-library non-KG artists route to the artist
    /// intents at all (JF-508 part A mechanism). The other 11 locales keep the
    /// built-in (raw text preserved there; swap to be evaluated separately).
    /// This set is the COMMITTED-model authority: MusicianSlotTypeTests pins its
    /// agreement with the model JSONs in both directions. Note the catalog-sync
    /// injection (CatalogManager) can additionally declare JellyfinArtist on
    /// synced locales outside this set; that deployed-vs-commited divergence and
    /// its single-sourcing decision are tracked as the JF-415 follow-up.
    /// </summary>
    public static readonly HashSet<string> CatalogBackedMusicianLocales = new(StringComparer.Ordinal)
    {
        "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "it-IT"
    };

    /// <summary>
    /// Resolves the slot type the musician slot declares for a locale: JellyfinArtist
    /// where the model is catalog-backed (JF-415), the AMAZON.Musician built-in
    /// elsewhere. The runtime target MUST use this value (why: the class doc, JF-332).
    /// </summary>
    /// <param name="locale">The Alexa locale (e.g. "it-IT").</param>
    /// <returns>The slot type name the locale's model declares for musician.</returns>
    public static string ResolveMusicianSlotType(string locale) =>
        CatalogBackedMusicianLocales.Contains(locale)
            ? CatalogSlotTypeNames[CatalogType.Artist]
            : Names[CatalogType.Artist];

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

    /// <summary>
    /// JF-727: the reverse of <see cref="CatalogSlotTypeNames"/>, keying a live
    /// model's slot-type name back to its catalog type for the wiring extraction
    /// (CatalogWiringGraft.ExtractWiring). Lives beside the forward map it
    /// inverts so the two directions cannot drift apart, and DERIVED from it so
    /// a fourth synced type's forward entry joins the reverse lookup by
    /// construction. The name-keyed extraction it feeds replaces the pre-JF-727
    /// per-type if/else that silently dropped a fourth type's catalog reference
    /// from a rebuild PUT (Apply would have re-PUT the rebuilt model unwired);
    /// the CatalogWiring record and InjectCatalogReferences stay positional on
    /// purpose (the JF-706 context boundary: a real fourth synced type forces
    /// those edits loudly through signature arity).
    /// INITIALIZATION CONTRACT: this field must stay declared AFTER
    /// <see cref="CatalogSlotTypeNames"/> (static field initializers run in
    /// textual order; inverting a not-yet-initialized forward map fails the
    /// whole type at first touch with TypeInitializationException). A duplicate
    /// VALUE in the forward map (two synced types sharing a slot-type name, a
    /// copy-paste edit) makes the ToDictionary below throw ArgumentException at
    /// type initialization, deliberately loud: the injection would otherwise
    /// fight over one model type block. Duplicates in a LIVE MODEL's types
    /// array are a different surface and stay last-wins in the extraction.
    /// GATE-MARKER TAIL: the reverse map holds the CONSTRUCTION-TIME view of
    /// the forward map; CatalogSlotTypeNames is a public mutable Dictionary,
    /// so any runtime mutation after first touch leaves this lookup stale
    /// (ExtractWiring would stop recognizing the mutated name). No writer
    /// exists today; a mutation feature must rebuild this map in the same
    /// change.
    /// </summary>
    private static readonly Dictionary<string, CatalogType> CatalogTypeBySlotTypeName =
        CatalogSlotTypeNames.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.Ordinal);

    /// <summary>
    /// Resolves the catalog type a slot-type name belongs to (JF-727); see
    /// <see cref="CatalogTypeBySlotTypeName"/>. Internal: only the wiring
    /// extraction (and its structure pin) needs the reverse direction.
    /// </summary>
    /// <param name="slotTypeName">The slot type name read from a live model's types array.</param>
    /// <param name="catalogType">The matching catalog type, when the name is a synced catalog slot type.</param>
    /// <returns>True when the name maps to a catalog type in sync scope.</returns>
    internal static bool TryGetCatalogTypeForSlotTypeName(string slotTypeName, out CatalogType catalogType) =>
        CatalogTypeBySlotTypeName.TryGetValue(slotTypeName, out catalogType);
}
