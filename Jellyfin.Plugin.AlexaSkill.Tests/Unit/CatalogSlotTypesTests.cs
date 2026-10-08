#nullable enable

using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-823: AudiobookTitle joins the STATIC catalog-sync family
/// (<see cref="CatalogSlotTypes.CatalogSlotTypeNames"/>). Until JF-823 the type
/// appeared only in the dynamic-entities table (<see cref="CatalogSlotTypes.Names"/>),
/// whose runtime push lands turn 2+ via Dialog.UpdateDynamicEntities, after
/// first-turn intent selection has already happened; the saved model's
/// AudiobookTitle vocabulary was the static seed for every user, so book titles
/// outside the seed lost the "l'audiolibro di" scoring race to
/// PlayNextEpisodeIntent (the JF-816 residual diagnosis). These pins hold the
/// forward entry and the JF-727 reverse-map derivation that keys
/// CatalogWiringGraft.ExtractWiring: a fourth synced type's forward entry joins
/// the reverse lookup BY CONSTRUCTION (the derived
/// CatalogTypeBySlotTypeName snapshots the forward map), and the pin proves it
/// did for this one.
/// </summary>
public class CatalogSlotTypesTests
{
    /// <summary>
    /// The forward entry: Audiobook syncs onto the slot type name every locale
    /// model already declares for PlayBookIntent.book. Without this entry the
    /// type is synced but never wired (InjectCatalogReferences' lookup by name
    /// misses), surfacing only later as a KeyNotFoundException mid-sync.
    /// </summary>
    [Fact]
    public void CatalogSlotTypeNames_Audiobook_MapsToAudiobookTitle()
    {
        Assert.Equal("AudiobookTitle", CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Audiobook]);
    }

    /// <summary>
    /// The JF-727 reverse-map derivation: the slot-type name a live model
    /// carries must resolve back to the catalog type the wiring extraction
    /// keys on. The reverse map is DERIVED from the forward map, so this pin
    /// cannot drift from the forward entry above without one of the two failing.
    /// </summary>
    [Fact]
    public void TryGetCatalogTypeForSlotTypeName_AudiobookTitle_DerivesAudiobook()
    {
        Assert.True(
            CatalogSlotTypes.TryGetCatalogTypeForSlotTypeName("AudiobookTitle", out var catalogType));
        Assert.Equal(CatalogType.Audiobook, catalogType);
    }

    /// <summary>
    /// The static family stays distinct from the dynamic-entities table: the
    /// dynamic target for Audiobook is the same NAME (every locale declares
    /// AudiobookTitle), but the two tables answer different questions (runtime
    /// Dialog.UpdateDynamicEntities target vs saved-model catalog supplier);
    /// pinning the agreement prevents a future rename of one drifting the other
    /// silently.
    /// </summary>
    [Fact]
    public void Audiobook_StaticAndDynamicSlotTypeNames_Agree()
    {
        Assert.Equal(
            CatalogSlotTypes.Names[CatalogType.Audiobook],
            CatalogSlotTypes.CatalogSlotTypeNames[CatalogType.Audiobook]);
    }

    /// <summary>
    /// The full static family after JF-823: four synced types. The exact-set
    /// pin keeps a copy-paste fifth entry (two synced types sharing a
    /// slot-type name) from reaching the reverse map, where it would throw at
    /// type initialization (deliberately loud, see CatalogSlotTypes).
    /// </summary>
    [Fact]
    public void CatalogSlotTypeNames_IsExactlyTheFourSyncedTypes()
    {
        var entries = CatalogSlotTypes.CatalogSlotTypeNames
            .OrderBy(kvp => kvp.Key)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        Assert.Equal(
            new[]
            {
                (CatalogType.Album, "AlbumName"),
                (CatalogType.Artist, "JellyfinArtist"),
                (CatalogType.Audiobook, "AudiobookTitle"),
                (CatalogType.Series, "SeriesName")
            }.OrderBy(t => t.Item1),
            entries.Select(kvp => (kvp.Key, kvp.Value)));
    }
}
