#nullable enable

using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-737: the behavioral half of the emptiness-pre-check closure. The
/// pre-JF-737 inline check (typeLegs.All(leg => leg.Items.Count == 0) in
/// SyncUserLibraryAsync) had no pin because over the CURRENT three rows
/// All() and a hand three-term conjunction are behaviorally identical, and
/// the wiring table was a method-local the tests could not feed a
/// distinguishing library through (CatalogType's sync scope is three
/// members). Naming the predicate
/// (<see cref="LibrarySyncService.AllTypeLegsEmpty"/>) makes its PARAMETER a
/// test-constructible collection, so the discriminating input the sync
/// itself cannot produce (a leg beyond the current three holding the
/// library's only items) is fabricated here. No plugin fixture needed: the
/// predicate is a pure static function over the leg shape. The routing half
/// (the check calls the predicate, not an inline derivation) is the IL pin
/// in <see cref="LibrarySyncServiceStructureTests"/>.
/// </summary>
public class LibrarySyncServiceEmptinessPrecheckTests
{
    /// <summary>
    /// The JF-737 filed hazard, made loud: a hand conjunction over the first
    /// three legs (legs[0].Items.Count == 0 &amp;&amp; legs[1]... &amp;&amp;
    /// legs[2]...) returns TRUE for this table (all three empty) and would
    /// take the emptiness skip, so a library holding only the fourth synced
    /// type's items never syncs, silently; the All-over-the-full-table
    /// definition returns false and the sync runs. Over the current three
    /// rows nothing discriminates the two derivations - this fourth leg is
    /// the entire difference, and it exists only at the predicate level.
    /// </summary>
    [Fact]
    public void AllTypeLegsEmpty_LegBeyondTheCurrentThreeHoldingTheOnlyItems_ReturnsFalse_JF737()
    {
        var fourthTypeOnlyLibrary = Legs(0, 0, 0, 1);

        Assert.False(LibrarySyncService.AllTypeLegsEmpty(fourthTypeOnlyLibrary));
    }

    /// <summary>
    /// The truth table's other rows, so the discriminator above cannot pass
    /// vacuously through an inverted predicate: an all-empty table is empty
    /// (the sync's real skip shape), and a non-empty FIRST leg is not empty
    /// (the row the pre-JF-727 conjunction and the All() derivation already
    /// agreed on).
    /// </summary>
    [Fact]
    public void AllTypeLegsEmpty_AllEmptyIsTrue_AndFirstLegNonEmptyIsFalse()
    {
        Assert.True(LibrarySyncService.AllTypeLegsEmpty(Legs(0, 0, 0)));
        Assert.False(LibrarySyncService.AllTypeLegsEmpty(Legs(1, 0, 0)));
    }

    /// <summary>
    /// Legs matching the wiring table's row shape with the given per-leg item
    /// counts. The emptiness fact reads ONLY Items.Count, so the items are
    /// null-element arrays (nothing ever dereferences them: the predicate
    /// counts, the sync's fetch replaces) and the non-emptiness axes
    /// (CatalogType, Kind, the row delegates, the names) are placeholders.
    /// Gate-marker tail (code-review F4): a future contract that reads
    /// beyond Items.Count - the Kind, the delegates, or the items
    /// themselves - must re-judge these placeholders per addition; note a
    /// predicate that starts dereferencing items NREs on the null elements
    /// and fails these pins loudly, which is the wanted vigilance, not a
    /// hole.
    /// </summary>
    private static (CatalogType Type, BaseItemKind Kind, IReadOnlyList<BaseItem> Items, Func<string?> StoredCatalogId, Action<string> StoreCatalogId, Action<int> StoreCount, string Name, string Description)[] Legs(
        params int[] itemCounts)
    {
        var legs = new (CatalogType Type, BaseItemKind Kind, IReadOnlyList<BaseItem> Items, Func<string?> StoredCatalogId, Action<string> StoreCatalogId, Action<int> StoreCount, string Name, string Description)[itemCounts.Length];
        for (int i = 0; i < itemCounts.Length; i++)
        {
            legs[i] = (
                CatalogType.Artist,
                BaseItemKind.MusicArtist,
                new BaseItem[itemCounts[i]],
                () => null,
                _ => { },
                _ => { },
                string.Empty,
                string.Empty);
        }

        return legs;
    }
}
