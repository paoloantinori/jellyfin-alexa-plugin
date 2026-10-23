using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Direct pins for <see cref="CatalogPayload.TruncateTo"/> (JF-879): the
/// static tail is reserved on ANY over-cap cut, not only at exact library
/// saturation (JF-848's partition). The sync-level pins in
/// LibrarySyncServiceCatalogCapTests cover the wired path; these cover the
/// partition arithmetic itself, including shapes production never reaches.
/// </summary>
public class CatalogPayloadTruncateToTests
{
    private static CatalogPayload Build(int libraryCount, int tailCount, string? lastTailName = null)
    {
        // The library block rides the production builder FromItems (unique
        // fixture names, so its same-title dedup is inert); the synthetic
        // tail has no shared helper (MergeSeeds only emits the real seed
        // names), so it appends through CatalogValueFactory.Create: the
        // JF-689 construction contract (truncation, enrichment) then shapes
        // fixture tail entries exactly as it shapes production ones, so a
        // pin cannot drift from real factory output.
        var payload = CatalogPayload.FromItems(
            CatalogType.Audiobook,
            Enumerable.Range(1, libraryCount)
                .Select(i => (Guid.NewGuid(), $"Lib {i:D3}")),
            (_, _) => [],
            "it-IT");

        for (int i = 1; i <= tailCount; i++)
        {
            string name = i == tailCount && lastTailName != null ? lastTailName : $"Seed {i:D3}";
            if (CatalogValueFactory.Create(
                    CatalogType.Audiobook, Guid.NewGuid(), name, []) is { } entry)
            {
                payload.Values.Add(entry);
            }
        }

        return payload;
    }

    private static string[] Names(CatalogPayload payload) =>
        payload.Values.Select(v => v.Name.Value).ToArray();

    /// <summary>
    /// The identity pin: a payload at or under the cap is byte-identical in
    /// and out, nothing dropped, nothing reserved (JF-848/JF-879 both keep
    /// this).
    /// </summary>
    [Theory]
    [InlineData(10, 2, 12)]
    [InlineData(10, 2, 13)]
    public void TruncateTo_PayloadFits_NoDropNoReserve(int library, int tail, int cap)
    {
        var payload = Build(library, tail);
        var before = Names(payload);

        var (dropped, droppedReserved) = payload.TruncateTo(cap, tail);

        Assert.Empty(dropped);
        Assert.Equal(0, droppedReserved);
        Assert.Equal(before, Names(payload));
    }

    /// <summary>
    /// THE JF-879 BAND PIN: a library one value under the cap with a tail
    /// that no longer fits whole. The tail survives whole and the LIBRARY
    /// HEAD loses the excess (the reserve-on-any-cut policy; rationale at
    /// <see cref="CatalogPayload.TruncateTo"/>).
    /// </summary>
    [Fact]
    public void TruncateTo_JustBelowCapLibrary_ReservesWholeTail()
    {
        // 24 library + 6 tail = 30 values, cap 25: the library block alone
        // (24) stays under the cap, so JF-848's condition never fired here.
        var payload = Build(24, 6);
        string[] originalNames = Names(payload);

        var (dropped, droppedReserved) = payload.TruncateTo(25, 6);

        Assert.Equal(25, payload.Values.Count);
        // The kept head is the library block's first (cap - tail) values in
        // fetch order, pinned in full: the tail-survival and drop assertions
        // alone would also pass a scrambled head.
        Assert.Equal(
            originalNames.Take(25 - 6),
            payload.Values.Take(25 - 6).Select(v => v.Name.Value));

        // The tail survives whole, in order, as the final block.
        Assert.Equal(
            new[] { "Seed 001", "Seed 002", "Seed 003", "Seed 004", "Seed 005", "Seed 006" },
            payload.Values.Skip(25 - 6).Select(v => v.Name.Value));
        Assert.Equal(5, dropped.Count);
        Assert.Equal(0, droppedReserved);
        // The drop leads with the first book past the reserved library head.
        Assert.Equal("Lib 020", dropped[0].Name.Value);
        Assert.Equal("Lib 024", dropped[^1].Name.Value);
    }

    /// <summary>
    /// The seedless degenerate: reservedTailCount 0 over the cap is the
    /// plain tail cut, byte-identical to the pre-JF-848 behavior (library
    /// head kept, library tail dropped, zero static drops reported).
    /// </summary>
    [Fact]
    public void TruncateTo_SeedlessOverCap_PlainTailCut()
    {
        var payload = Build(8, 0);

        var (dropped, droppedReserved) = payload.TruncateTo(5, 0);

        Assert.Equal(5, payload.Values.Count);
        Assert.Equal(
            new[] { "Lib 001", "Lib 002", "Lib 003", "Lib 004", "Lib 005" },
            Names(payload));
        Assert.Equal(3, dropped.Count);
        Assert.Equal(0, droppedReserved);
    }

    /// <summary>
    /// The strict-over-cap shape with a generic word riding the tail: the
    /// word (the FINAL static entry) survives as the payload's last value.
    /// </summary>
    [Fact]
    public void TruncateTo_StrictlyOverCap_GenericWordSurvivesLast()
    {
        // The generic word rides the tail as a factory-built entry (the
        // production path; see Build), never a post-hoc name mutation.
        var payload = Build(30, 3, "generic");

        var (dropped, droppedReserved) = payload.TruncateTo(25, 3);

        Assert.Equal(25, payload.Values.Count);
        Assert.Equal("generic", payload.Values[^1].Name.Value);
        Assert.Equal(0, droppedReserved);
        Assert.All(dropped, d => Assert.NotEqual("generic", d.Name.Value));
    }

    /// <summary>
    /// The seam-only extreme: a reserved tail larger than the cap itself.
    /// The library drops whole, the tail keeps its head, its excess drops
    /// after it, and the cap still binds.
    /// </summary>
    [Fact]
    public void TruncateTo_TailLargerThanCap_LibraryDropsWholeTailExcessDrops()
    {
        var payload = Build(4, 8);

        var (dropped, droppedReserved) = payload.TruncateTo(5, 8);

        Assert.Equal(5, payload.Values.Count);
        Assert.Equal(
            new[] { "Seed 001", "Seed 002", "Seed 003", "Seed 004", "Seed 005" },
            Names(payload));
        Assert.Equal(7, dropped.Count);
        Assert.Equal(3, droppedReserved);
        Assert.Equal("Lib 001", dropped[0].Name.Value);
        // The tail excess leads with the first seed past the cap.
        Assert.Equal("Seed 006", dropped[4].Name.Value);
        Assert.Equal("Seed 008", dropped[^1].Name.Value);
    }
}
