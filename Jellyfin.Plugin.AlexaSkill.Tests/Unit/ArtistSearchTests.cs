using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests the in-memory tier-4 (fuzzy-all) path of <see cref="ArtistSearch.SearchAsync"/>
/// and the JF-377 <see cref="ArtistSearch.IsCoincidentalContainmentMatch"/> predicate
/// that flags coincidental substring matches (a short common-word artist name hidden
/// inside a longer query) for handler-side downgrade to a yes/no disambiguation.
/// </summary>
public class ArtistSearchTests
{
    private static readonly ILogger Logger = new Mock<ILogger>().Object;

    [Fact]
    public async Task SearchAsync_Tier4_NonsenseQueryContainingCommonWordArtist_ReturnsMatch()
    {
        // JF-377 repro at the SearchAsync layer: a literal artist named "artist" DOES surface from
        // tier-4 (the containment shortcut scores it 90 >= threshold 60). SearchAsync does not
        // judge whether the match is coincidental; it returns it. The PlayArtistSongs handler's
        // JF-377 branch then detects the coincidental-containment shape and downgrades the single
        // match to a yes/no disambiguation instead of auto-playing it. The downgrade is tested at
        // the IsCoincidentalContainmentMatch predicate level and live-verified on minix.
        var artist = new MusicArtist { Name = "artist", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { artist });

        var result = await ArtistSearch.SearchAsync(
            "zzzqqq nonexistent artist",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("artist", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_Tier4_RealSingleWordArtistInCarrierPhrase_ReturnsMatch()
    {
        // JF-377 (disambiguation-downgrade architecture): SearchAsync RETURNS the tier-4 match
        // even when it is a coincidental-containment shape. The decision to downgrade to a
        // disambiguation prompt (rather than auto-play) is the handler's job, not SearchAsync's.
        // So a real single-word artist ("Bush") inside a carrier phrase ("suona la musica di
        // bush") that bled into the raw slot value still surfaces from tier-4 for the handler
        // to offer via yes/no. See PlayArtistSongsIntentHandler JF-377 branch.
        var artist = new MusicArtist { Name = "Bush", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { artist });

        var result = await ArtistSearch.SearchAsync(
            "suona la musica di bush",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Bush", result[0].Name);
    }

    [Fact]
    public void IsCoincidentalContainmentMatch_NonsenseQueryContainingCommonWord_True()
    {
        // The downgrade trigger: a short common-word candidate contained as one content word of
        // a longer nonsense query is coincidental containment. it-IT stop words (di) are stripped,
        // leaving [zzzqqq, nonexistent, artist], artist covers 1/3 -> true.
        Assert.True(ArtistSearch.IsCoincidentalContainmentMatch("zzzqqq nonexistent artist", "artist", "it-IT"));
    }

    [Fact]
    public void IsCoincidentalContainmentMatch_RealArtistInCarrierPhrase_True_Too()
    {
        // The string-indistinguishability lock (research jf377_discriminator): a real artist
        // ("Bush") inside a carrier phrase that bled into the slot is ALSO coincidental
        // containment by the predicate. This is WHY the handler downgrades to a yes/no prompt
        // (the user says "yes" and Bush plays) rather than rejecting. Same predicate, both
        // cases -> both get the prompt, neither is silently auto-played or silently rejected.
        Assert.True(ArtistSearch.IsCoincidentalContainmentMatch("suona la musica di bush", "Bush", "it-IT"));
    }

    [Fact]
    public void IsCoincidentalContainmentMatch_GenuineFuzzyDistanceMatch_False()
    {
        // No-regression lock on the trigger: a genuine Levenshtein tier-4 match (candidate NOT a
        // substring of the query) is NOT coincidental containment, so the handler auto-plays it
        // unchanged. "radiohed" -> "Radiohead": Radiohead is not a substring of radiohed.
        Assert.False(ArtistSearch.IsCoincidentalContainmentMatch("radiohed", "Radiohead", "en-US"));
    }

    [Fact]
    public void IsCoincidentalContainmentMatch_InteriorContainmentSingleTokenQuery_True()
    {
        // JF-408 residual (found via the simulator on the deployed build): the query is ONE
        // content token, so the coverage rule cannot see the coincidence. The library had an
        // artist literally named "artist" (garbage metadata) and "xyznonexistentartist123"
        // auto-played it. A containment whose every occurrence is strictly INTERIOR (word
        // characters on both sides) is riding inside another word and is coincidental.
        Assert.True(ArtistSearch.IsCoincidentalContainmentMatch("xyznonexistentartist123", "artist", "it-IT"));
    }

    [Fact]
    public void IsCoincidentalContainmentMatch_PluralAffixedSingleToken_False()
    {
        // No-regression lock for the interior rule: "outkasts" -> "outkast" is a PREFIX-shaped
        // containment in a single-token query (a plural/affixed form of the real name) and must
        // keep auto-playing, not be downgraded to a prompt.
        Assert.False(ArtistSearch.IsCoincidentalContainmentMatch("outkasts", "outkast", "en-US"));
    }

    [Fact]
    public async Task SearchAsync_Tier4_RealMultiWordNearMatch_StillResolves()
    {
        // No-regression: a genuine near-match where the candidate is contained in the query
        // but covers a fair fraction of its words must still resolve. "Pink Floyd" sits
        // inside "the artist pink floyd" and covers 2 of 4 words (not a minority).
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { artist });

        var result = await ArtistSearch.SearchAsync(
            "the artist pink floyd",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Pink Floyd", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_Tier4_AsrTruncation_CandidateLongerThanQuery_StillResolves()
    {
        // No-regression: ASR truncation (short query, longer candidate) is the intended
        // containment case the guard must never touch. The candidate is longer than the
        // query, so the guard returns false immediately.
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { artist });

        var result = await ArtistSearch.SearchAsync(
            "pink",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Pink Floyd", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_Tier4_GenuineFuzzyDistanceMatch_StillResolves()
    {
        // No-regression: a single-word ASR-garbled query that matches on Levenshtein distance
        // ("radiohed" -> "Radiohead", one deletion) is NOT a substring-containment match, so
        // the guard never applies and the genuine fuzzy match resolves. Single-word queries
        // are exempt from the guard regardless (no unrelated noise to hide in).
        var artist = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { artist });

        var result = await ArtistSearch.SearchAsync(
            "radiohed",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Radiohead", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_Tier4_PartialTokenSubstring_ReturnsMatch()
    {
        // Boundary: candidate "art" is a substring of the query token "artist". SearchAsync
        // returns it from tier-4 (it does not judge coincidence). The coincidental-containment
        // judgment (whether "art" covers too few query content words) is the predicate's job,
        // exercised in the IsCoincidentalContainmentMatch_* tests above and applied by the
        // PlayArtistSongs handler.
        var artist = new MusicArtist { Name = "art", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { artist });

        var result = await ArtistSearch.SearchAsync(
            "xyzzyfoo artist band",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("art", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_CoincidentalContainment_Gated_PhoneticTierResolvesKoop()
    {
        // Live 2026-08-28 21:17: "un disco dei Koop" spoken fast, ASR "cup". SearchAsync's
        // ungated tier-1 returned "Porcupine Tree" ("cup" substring) and stopped the chain,
        // so the album path played the wrong artist; the inline PlayArtistSongs search,
        // which has the JF-381 gate, correctly fell through to the phonetic tier. With the
        // gate, tier 1 is empty and tier 4's phonetic match resolves cup -> Koop (both
        // code KP, length-matched, floored above the containment score).
        var koop = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var porcupine = new MusicArtist { Name = "Porcupine Tree", Id = Guid.NewGuid() };
        var codes = new Dictionary<Guid, (string Primary, string? Alternate)>
        {
            [koop.Id] = ("KP", null),
            [porcupine.Id] = ("PRKPN", null),
        };
        var index = new FakeArtistIndex(new[] { koop, porcupine }, codes);

        var result = await ArtistSearch.SearchAsync(
            "cup",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Koop", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_DatabaseFallback_CoincidentalContainment_GatedEverywhere()
    {
        // JF-381 sweep: the DATABASE fallback tiers (SearchTerm tier-1 and the
        // NameContains tier-4) return raw results, so the same "cup" -> "Porcupine Tree"
        // coincidence that hit the in-memory tier-1 (live 2026-08-28 21:17) could surface
        // whenever the artist index is not ready. Both DB tiers must gate the band.
        var porcupine = new MusicArtist { Name = "Porcupine Tree", Id = Guid.NewGuid() };

        Task<IReadOnlyList<BaseItem>> DbQuery(InternalItemsQuery q, CancellationToken t)
        {
            // SearchTerm / NameContains queries surface the coincidental substring;
            // prefix queries (tier 2/3) legitimately find nothing.
            IReadOnlyList<BaseItem> result = q.NameStartsWith != null
                ? Array.Empty<BaseItem>()
                : new[] { porcupine };
            return Task.FromResult(result);
        }

        var result = await ArtistSearch.SearchAsync(
            "cup",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: null,
            logger: Logger,
            dbQuery: DbQuery,
            cancellationToken: CancellationToken.None);

        Assert.DoesNotContain(result, a => a.Name == "Porcupine Tree");
    }

    [Fact]
    public async Task SearchAsync_PartialFirstWordMatch_DeferredMatchStillWinsAtTier4()
    {
        // JF-417 review correction: without the exclusion, the deferred candidate
        // ("P!nk") still wins at tier-4 because the containment exemption gives it
        // ContainmentScore (90), beating "Pink Floyd" (~91 via phonetic floor but
        // losing to the containment early-exit in index order). The P!nk floyd case
        // is handled by the ALBUM path (PlayAlbumIntent + catalog AlbumName entity
        // resolution, live-verified via web console 2026-08-30), NOT the artist path.
        // The artist path returns the deferred match when tiers 3-4 don't find a
        // DIFFERENT winner.
        var pnk = new MusicArtist { Name = "P!nk", Id = Guid.NewGuid() };
        var pinkFloyd = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { pnk, pinkFloyd });

        var result = await ArtistSearch.SearchAsync(
            "P!nk floyd",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        // The deferred P!nk is still returned (containment wins at tier-4)
        Assert.Single(result);
        Assert.Equal("P!nk", result[0].Name);
    }

    [Fact]
    public void IsPartialFirstWordMatch_FullCoverageCandidate_False()
    {
        // Direct predicate test (review finding: the SearchAsync test never reaches
        // the guard because tier-1 short-circuits on "the beatles" -> "The Beatles").
        // "The Beatles" (11 chars) exceeds firstWord "the" (3) + 2 = 5, so the
        // candidateIsJustFirstWord condition is false.
        Assert.False(ArtistSearch.IsPartialFirstWordMatch("the beatles", "the", "The Beatles"));
    }

    [Fact]
    public void IsPartialFirstWordMatch_PartialCandidate_True()
    {
        // Direct predicate test: "P!nk" (4 chars) is just the first word "P!nk" (4 chars),
        // and 4 < 10 * 0.5 = 5.
        Assert.True(ArtistSearch.IsPartialFirstWordMatch("P!nk floyd", "P!nk", "P!nk"));
    }

    [Fact]
    public void IsPartialFirstWordMatch_SingleWordQuery_False()
    {
        // Single-word query: guard never fires.
        Assert.False(ArtistSearch.IsPartialFirstWordMatch("crash", "crash", "Crash Test Dummies"));
    }

    [Fact]
    public async Task SearchAsync_PartialFirstWordMatch_NoBetterMatch_FallsBackToTier2()
    {
        // JF-417: when the guard defers tier-2 but tiers 3-4 find nothing better,
        // the deferred tier-2 match is still the best available answer.
        var pnk = new MusicArtist { Name = "P!nk", Id = Guid.NewGuid() };
        var radiohead = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { pnk, radiohead });

        var result = await ArtistSearch.SearchAsync(
            "P!nk something",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        // Tier-4 fuzzy-all won't find anything better for "P!nk something" than
        // "P!nk" (Radiohead is too different), so the deferred tier-2 wins.
        Assert.Single(result);
        Assert.Equal("P!nk", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_SingleWordQuery_PrefixMatchNotDeferred()
    {
        // JF-417 guard is multi-word only: the ASR-truncation shape "crash" ->
        // "Crash Test Dummies" must keep working via tier-2 without deferral.
        var crashTestDummies = new MusicArtist { Name = "Crash Test Dummies", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { crashTestDummies });

        var result = await ArtistSearch.SearchAsync(
            "crash",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Crash Test Dummies", result[0].Name);
    }

    [Fact]
    public async Task SearchAsync_FullCoveragePrefixMatch_NotDeferred()
    {
        // JF-417: when the candidate covers the full query content ("The Beatles"
        // for "the beatles"), the guard must not fire even though the query is
        // multi-word and the first word is short.
        var beatles = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { beatles });

        var result = await ArtistSearch.SearchAsync(
            "the beatles",
            user: null,
            libraryManager: Mock.Of<ILibraryManager>(),
            artistIndex: index,
            logger: Logger,
            dbQuery: NotCalled,
            cancellationToken: CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("The Beatles", result[0].Name);
    }

    /// <summary>
    /// JF-419.2 choke point: the IsReady gate at the top of SearchAsync covers every
    /// ArtistSearch caller; handlers with cold non-artist paths also call
    /// EnsureReady at entry (two-layer design, see IndexWarmingGate doc). Thrown,
    /// not returned, so the request pipeline translates it into the SkillWarmingUp Tell.
    /// </summary>
    [Fact]
    public async Task SearchAsync_IndexPresentButWarming_Throws()
    {
        await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            ArtistSearch.SearchAsync(
                "pink floyd",
                user: null,
                libraryManager: Mock.Of<ILibraryManager>(),
                artistIndex: Mock.Of<IArtistIndex>(i => i.IsReady == false),
                logger: Logger,
                dbQuery: NotCalled,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// Review round 1 finding 2: a DISABLED index (gave up after repeated load
    /// failures) is treated as absent - the gate passes so callers degrade to their
    /// database paths instead of an endless warming refusal.
    /// </summary>
    [Fact]
    public void IndexWarmingGate_DisabledIndex_DoesNotThrow()
    {
        global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.IndexWarmingGate.EnsureReady(Mock.Of<IArtistIndex>(i => i.IsReady == false && i.IsDisabled == true));
        global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.IndexWarmingGate.EnsureReady(Mock.Of<ISongNgramIndex>(i => i.IsReady == false && i.IsDisabled == true));
    }

    private static Task<IReadOnlyList<BaseItem>> NotCalled(
        InternalItemsQuery q, CancellationToken t) =>
        throw new InvalidOperationException("In-memory path must not hit the database");

    /// <summary>
    /// Minimal ready artist index backed by a fixed list, with no phonetic codes so the
    /// non-phonetic FuzzyMatcher.FindBestMatch overload is exercised (the tier-4 path under
    /// test uses phonetic lookup only when codes are present).
    /// </summary>
    private sealed class FakeArtistIndex : IArtistIndex
    {
        private readonly IReadOnlyList<BaseItem> _artists;
        private readonly Dictionary<Guid, (string Primary, string? Alternate)> _phoneticCodes;

        public FakeArtistIndex(IEnumerable<BaseItem> artists, Dictionary<Guid, (string Primary, string? Alternate)>? phoneticCodes = null)
        {
            _artists = artists.ToList();
            _phoneticCodes = phoneticCodes ?? new Dictionary<Guid, (string Primary, string? Alternate)>();
        }

        public bool IsReady => true;
        public bool IsDisabled => false;
        public int Count => _artists.Count;

        public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null) => _artists;

        public bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes)
        {
            codes = default;
            return _phoneticCodes.TryGetValue(artistId, out codes);
        }
    }
}
