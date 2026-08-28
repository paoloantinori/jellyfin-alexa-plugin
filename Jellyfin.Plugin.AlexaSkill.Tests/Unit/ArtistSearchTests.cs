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

        public FakeArtistIndex(IEnumerable<BaseItem> artists) => _artists = artists.ToList();

        public bool IsReady => true;
        public int Count => _artists.Count;

        public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null) => _artists;

        public bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes)
        {
            codes = default;
            return false;
        }
    }
}
