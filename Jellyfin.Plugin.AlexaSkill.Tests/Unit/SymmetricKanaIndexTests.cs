using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-755 red proofs for the symmetric index-side kana normalization: the artist
/// search chain reaches a KANA-TAGGED artist through its indexed romaji key
/// (item 4 of the JF-645 split), and the 2026-09-29 JF-658 finding (moved here
/// from JF-645 item 4): a KANA canonical against a mixed library must resolve to
/// the kana-named artist it points at, never weak-hit the Latin 91-tie class the
/// JF-652 bar cannot see for canonical-bearing queries. Handler-level pins for the
/// same finding live in MusicianErCanonicalTests; the index-build pins in
/// ArtistIndexServiceTests and SongNgramIndexServiceTests.
/// </summary>
public class SymmetricKanaIndexTests
{
    private static readonly ILogger Logger = new Mock<ILogger>().Object;

    private static BaseItem KanaQueen(Guid id) => new MusicArtist { Name = "クイーン", Id = id };
    private static BaseItem Queen(Guid id) => new MusicArtist { Name = "Queen", Id = id };
    private static BaseItem Keane(Guid id) => new MusicArtist { Name = "Keane", Id = id };

    private static Task<IReadOnlyList<BaseItem>> NotCalled(InternalItemsQuery q, CancellationToken t) =>
        throw new InvalidOperationException("In-memory path must not hit the database");

    // ---------------------------------------------------------------
    // The moved JF-658 finding: the kana canonical in a mixed library
    // ---------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_KanaCanonical_MixedLibrary_ReachesKanaArtistNotLatinTie_JF755()
    {
        // The finding's shape: ER resolved the slot to a KANA canonical ('クイーン',
        // a kana-tagged catalog value) while the library holds that kana-named
        // artist plus the Queen/Keane Double Metaphone-colliding pair. SearchAsync
        // romanizes the canonical at entry ('kuin'); without the symmetric index
        // tier 4 coin-flips the 91-tie to whichever Latin artist the iteration
        // order favors. The romaji key must make tier 1 exact-hit the kana artist.
        var kana = KanaQueen(Guid.NewGuid());
        var queen = Queen(Guid.NewGuid());
        var keane = Keane(Guid.NewGuid());
        var index = new FakeArtistIndex(
            new[] { kana, queen, keane },
            FakeArtistIndex.CodesFromArtistNames(kana, queen, keane));

        var result = await ArtistSearch.SearchAsync(
            "クイーン",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            locale: "ja-JP",
            cancellationToken: CancellationToken.None);

        var match = Assert.Single(result);
        Assert.Equal(kana.Id, match.Id);
        Assert.Equal("クイーン", match.Name);
    }

    [Fact]
    public async Task SearchAsync_LatinIterationOrderSwapped_StillKanaArtist_JF755()
    {
        // Same shape with the Latin pair listed FIRST: pre-fix the 91-tie resolved
        // by iteration order, so both orders must be pinned, not just the one where
        // the kana artist happens to sit at the tie's front.
        var queen = Queen(Guid.NewGuid());
        var keane = Keane(Guid.NewGuid());
        var kana = KanaQueen(Guid.NewGuid());
        var index = new FakeArtistIndex(
            new[] { queen, keane, kana },
            FakeArtistIndex.CodesFromArtistNames(queen, keane, kana));

        var result = await ArtistSearch.SearchAsync(
            "クイーン",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            locale: "ja-JP",
            cancellationToken: CancellationToken.None);

        var match = Assert.Single(result);
        Assert.Equal(kana.Id, match.Id);
    }

    // ---------------------------------------------------------------
    // Item 4 proper: the kana-tagged library becomes reachable
    // ---------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_RawKanaQuery_KanaTaggedLibrary_FindsKanaArtist_JF755()
    {
        // The JF-643 accepted narrowing closed: a kana query ('クイーン', no ER) against
        // a KATAKANA-TAGGED name previously exact-matched on the Contains tier, then
        // missed every tier once the query side romanized. The romaji key restores
        // the reachability without transliterating the stored name.
        var kana = KanaQueen(Guid.NewGuid());
        var index = new FakeArtistIndex(
            new[] { kana }, FakeArtistIndex.CodesFromArtistNames(kana));

        var result = await ArtistSearch.SearchAsync(
            "クイーン",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            locale: "ja-JP",
            cancellationToken: CancellationToken.None);

        var match = Assert.Single(result);
        Assert.Equal(kana.Id, match.Id);
    }

    [Fact]
    public async Task SearchAsync_LatinRomajiQuery_KanaTaggedLibrary_FindsKanaArtist_JF755()
    {
        // The bridge from the LATIN side: a romaji query 'kuin' (spoken or typed)
        // against the kana-tagged name reaches the artist through the same key,
        // while the Latin 91-tie rivals stay out of the result.
        var kana = KanaQueen(Guid.NewGuid());
        var keane = Keane(Guid.NewGuid());
        var index = new FakeArtistIndex(
            new[] { kana, keane }, FakeArtistIndex.CodesFromArtistNames(kana, keane));

        var result = await ArtistSearch.SearchAsync(
            "kuin",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            locale: "ja-JP",
            cancellationToken: CancellationToken.None);

        var match = Assert.Single(result);
        Assert.Equal(kana.Id, match.Id);
    }

    [Fact]
    public async Task SearchAsync_LatinLibrary_RomajiLegIsInert_JF755()
    {
        // Control: an all-Latin library has no romaji keys, so the tier legs are
        // byte-identical to the pre-JF-755 behavior (the exact Latin hit still
        // resolves at tier 1).
        var queen = Queen(Guid.NewGuid());
        var keane = Keane(Guid.NewGuid());
        var index = new FakeArtistIndex(
            new[] { queen, keane }, FakeArtistIndex.CodesFromArtistNames(queen, keane));

        var result = await ArtistSearch.SearchAsync(
            "queen",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            locale: "en-US",
            cancellationToken: CancellationToken.None);

        var match = Assert.Single(result);
        Assert.Equal(queen.Id, match.Id);
    }

    // ---------------------------------------------------------------
    // The ONE resolver, direct pins
    // ---------------------------------------------------------------

    [Fact]
    public void QueryNameFor_KanaNamedArtist_ResolvesRomajiKey_JF755()
    {
        var kana = KanaQueen(Guid.NewGuid());
        var index = new FakeArtistIndex(new[] { kana });

        Assert.Equal("kuin", ArtistSearch.QueryNameFor(index, kana));
    }

    [Fact]
    public void QueryNameFor_LatinArtistOrNoIndex_ResolvesRawName_JF755()
    {
        var latin = Queen(Guid.NewGuid());
        var index = new FakeArtistIndex(new[] { latin });

        Assert.Equal("Queen", ArtistSearch.QueryNameFor(index, latin));
        Assert.Equal("Queen", ArtistSearch.QueryNameFor(null, latin));
    }

    [Fact]
    public void ScoreBestWithCodes_RomanizedQuery_KanaArtist_ScoresExactNotFloor_JF755()
    {
        // The bar-scoring leg: the kana-named artist's matcher score against the
        // romanized query must be the exact 100 (clear margin over the Latin pair's
        // 91 floor), so the JF-652 near-tie margin auto-plays the right artist
        // instead of prompting.
        var kana = KanaQueen(Guid.NewGuid());
        var queen = Queen(Guid.NewGuid());
        var keane = Keane(Guid.NewGuid());
        var index = new FakeArtistIndex(
            new[] { kana, queen, keane },
            FakeArtistIndex.CodesFromArtistNames(kana, queen, keane));

        var scored = ArtistSearch.ScoreBestWithCodes("kuin", new[] { kana, queen, keane }, index);
        Assert.NotNull(scored);
        Assert.Equal(kana.Id, scored!.Value.Item.Id);
        Assert.Equal(100, scored.Value.Score);
    }
}
