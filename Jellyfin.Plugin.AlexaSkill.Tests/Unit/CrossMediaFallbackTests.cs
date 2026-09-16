using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Characterization tests for the JF-315 cluster-F cross-media-fallback members
/// (FindBestNonEmbeddedMatch, PassesCrossMediaWordGuard, GetCrossMediaArtistSuggestion,
/// BuildCrossMediaArtistOfferAsk, PassesArtistMatchAcceptance, ApplyAnnouncement),
/// written green against the pre-extraction BaseHandler code BEFORE the move to the
/// CrossMediaFallback collaborator (JF-315 batch 7) via a probe subclass, then
/// retargeted to the collaborator after the move. The thin spots pinned here (all
/// previously covered only INDIRECTLY through handler tests, if at all): the JF-412
/// embedded-winner walk-down loop taken directly (skip the embedded candidate, keep
/// the next eligible one, exhaust to null), the JF-446/JF-479 word-level content
/// guard (fragment tokens out, spoken-word count judged), the JF-363
/// per-user/global suggestion resolution, the offer-ask session-attribute wiring
/// (disambig_type=artist + the crossmedia_notfound_* decline contract, both media
/// types), the JF-471 acceptance gate's plain and phonetic branches, and the JF-345
/// announcement override. The big instance members (TryEntityFallbackAsync,
/// BuildArtistSongsResponseAsync, BuildSingleSongResponse, TrySongFallback) keep
/// their extensive handler-level coverage (CrossMediaTypeFallbackTests,
/// PlaySongAlbumFallbackTests, EntityFallbackTests, the music-gate suites, the
/// FoundSongInstead and warming-index suites) and are not re-pinned here.
/// </summary>
[Collection("Plugin")]
public class CrossMediaFallbackTests : PluginTestBase
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    // ---------------------------------------------------------------------
    // FindBestNonEmbeddedMatch (JF-412 walk-down)
    // ---------------------------------------------------------------------

    [Fact]
    public void FindBestNonEmbeddedMatch_NonEmbeddedAboveThreshold_Returned()
    {
        var svc = CreateService();
        var waltz = new MusicAlbum { Name = "Waltz for Koop", Id = Guid.NewGuid() };

        var match = svc.FindBestNonEmbeddedMatch("walls for cup", new[] { waltz }, a => a.Name!, threshold: 60);

        Assert.NotNull(match);
        Assert.Same(waltz, match!.Value.Item);
        Assert.True(match.Value.Score >= 60);
    }

    [Fact]
    public void FindBestNonEmbeddedMatch_BelowThreshold_ReturnsNull()
    {
        // The same 61-for-"Waltz for Koop" shape stays refused under the
        // containment-grade 90 bar the cross-media album cascade uses (JF-412).
        var svc = CreateService();
        var waltz = new MusicAlbum { Name = "Waltz for Koop", Id = Guid.NewGuid() };

        var match = svc.FindBestNonEmbeddedMatch("walls for cup", new[] { waltz }, a => a.Name!, threshold: 90);

        Assert.Null(match);
    }

    [Fact]
    public void FindBestNonEmbeddedMatch_EmbeddedWinnerSkipped_NextEligibleReturned()
    {
        // JF-412 live replay: for "walls for cup" the degenerate album "O" ranks FIRST
        // at 90 (the JF-408 embedded-containment shape, correctly refused) and "Waltz
        // for Koop" ties next at 61, above the default threshold 60. The guard must
        // block only the embedded candidate and let the next eligible match through.
        var svc = CreateService();
        var embedded = new MusicAlbum { Name = "O", Id = Guid.NewGuid() };
        var waltz = new MusicAlbum { Name = "Waltz for Koop", Id = Guid.NewGuid() };

        var match = svc.FindBestNonEmbeddedMatch(
            "walls for cup", new BaseItem[] { embedded, waltz }, a => a.Name!, threshold: 60);

        Assert.NotNull(match);
        Assert.Same(waltz, match!.Value.Item);
    }

    [Fact]
    public void FindBestNonEmbeddedMatch_AllWinnersEmbedded_ReturnsNull()
    {
        var svc = CreateService();
        var embedded = new MusicAlbum { Name = "O", Id = Guid.NewGuid() };

        var match = svc.FindBestNonEmbeddedMatch("walls for cup", new[] { embedded }, a => a.Name!, threshold: 60);

        Assert.Null(match);
    }

    // ---------------------------------------------------------------------
    // PassesCrossMediaWordGuard (JF-446 consolidation, JF-479 word-level count)
    // ---------------------------------------------------------------------

    [Fact]
    public void WordGuard_TwoContentWords_PassesWithStrippedTokens()
    {
        var svc = CreateService();

        bool passes = svc.PassesCrossMediaWordGuard("the pink floyd", "en-US", "artist", "Test", out string[] tokens);

        Assert.True(passes);
        // The tokens out param is the FRAGMENT-level stop-word-stripped join the
        // artist gate searches, not the word-level count the guard judges on.
        Assert.Equal(new[] { "pink", "floyd" }, tokens);
    }

    [Fact]
    public void WordGuard_ThreeContentWords_Rejected()
    {
        var svc = CreateService();

        bool passes = svc.PassesCrossMediaWordGuard("pink floyd band", "en-US", "artist", "Test", out _);

        Assert.False(passes);
    }

    [Fact]
    public void WordGuard_OnlyStopWords_Rejected()
    {
        var svc = CreateService();

        bool passes = svc.PassesCrossMediaWordGuard("the", "en-US", "artist", "Test", out _);

        Assert.False(passes);
    }

    [Fact]
    public void WordGuard_PunctuatedNameIsOneSpokenWord_JF479()
    {
        // JF-479: "P!nk floyd" is TWO spoken words (article-less stylized name +
        // surname), not the three fragments the tokenizer produces; counting
        // fragments rejected the live device shape, so only the COUNT is word-level.
        var svc = CreateService();

        bool passes = svc.PassesCrossMediaWordGuard("P!nk floyd", "en-US", "artist", "Test", out string[] tokens);

        Assert.True(passes);
        Assert.Contains("floyd", tokens);
    }

    // ---------------------------------------------------------------------
    // GetCrossMediaArtistSuggestion (JF-363 per-user/global resolution)
    // ---------------------------------------------------------------------

    [Fact]
    public void Suggestion_UserOverride_Wins()
    {
        var config = new PluginConfiguration { DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Confirm };
        var svc = CreateService(config);
        var user = TestHelpers.CreateTestUser();
        user.CrossMediaArtistSuggestion = CrossMediaArtistSuggestion.AutoServe;

        Assert.Equal(CrossMediaArtistSuggestion.AutoServe, svc.GetCrossMediaArtistSuggestion(user));
    }

    [Fact]
    public void Suggestion_UserWithoutOverride_FallsBackToGlobalDefault()
    {
        var config = new PluginConfiguration { DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.AutoServe };
        var svc = CreateService(config);
        var user = TestHelpers.CreateTestUser();
        user.CrossMediaArtistSuggestion = null;

        Assert.Equal(CrossMediaArtistSuggestion.AutoServe, svc.GetCrossMediaArtistSuggestion(user));
    }

    [Fact]
    public void Suggestion_NullUser_GlobalDefault()
    {
        var config = new PluginConfiguration { DefaultCrossMediaArtistSuggestion = CrossMediaArtistSuggestion.Off };
        var svc = CreateService(config);

        Assert.Equal(CrossMediaArtistSuggestion.Off, svc.GetCrossMediaArtistSuggestion(null));
    }

    // ---------------------------------------------------------------------
    // BuildCrossMediaArtistOfferAsk (JF-363 offer + decline contract)
    // ---------------------------------------------------------------------

    [Fact]
    public void OfferAsk_CarriesDisambiguationAndCrossMediaAttrs_SongType()
    {
        var svc = CreateService();
        var artist = new MusicArtist { Name = "Miles Davis", Id = Guid.NewGuid() };

        SkillResponse response = svc.BuildCrossMediaArtistOfferAsk("jazz cafe", artist, "en-US", DisambiguationHelper.MediaTypeSong);

        // Ask shape: the session stays open for the yes/no.
        Assert.False(response.Response.ShouldEndSession);
        Assert.Contains("jazz cafe", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Miles Davis", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response.Response.Reprompt);
        // The disambiguation state routes "yes" to PlayArtist...
        Assert.Equal("artist", response.SessionAttributes?[DisambiguationHelper.AttrType]);
        Assert.Equal(0, response.SessionAttributes?[DisambiguationHelper.AttrIndex]);
        Assert.Contains("Miles Davis", response.SessionAttributes?[DisambiguationHelper.AttrMatches]?.ToString() ?? string.Empty, StringComparison.Ordinal);
        // ...and the crossmedia_notfound_* pair routes "no" to the SONG not-found.
        Assert.Equal("jazz cafe", response.SessionAttributes?[DisambiguationHelper.AttrCrossmediaQuery]);
        Assert.Equal("song", response.SessionAttributes?[DisambiguationHelper.AttrCrossmediaType]);
    }

    [Fact]
    public void OfferAsk_AlbumDeclineType_Carried()
    {
        var svc = CreateService();
        var artist = new MusicArtist { Name = "Miles Davis", Id = Guid.NewGuid() };

        SkillResponse response = svc.BuildCrossMediaArtistOfferAsk("jazz cafe", artist, "en-US", DisambiguationHelper.MediaTypeAlbum);

        Assert.Equal("album", response.SessionAttributes?[DisambiguationHelper.AttrCrossmediaType]);
    }

    // ---------------------------------------------------------------------
    // PassesArtistMatchAcceptance (JF-471 decision-point predicate)
    // ---------------------------------------------------------------------

    [Fact]
    public void Acceptance_PlainBranch_ExactMatch_Passes()
    {
        var svc = CreateService();
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };

        bool passes = svc.PassesArtistMatchAcceptance(artist, "pink floyd", user: null, artistIndex: null, out int score);

        Assert.True(passes);
        Assert.True(score >= 90);
    }

    [Fact]
    public void Acceptance_PlainBranch_NonsenseQuery_Rejected()
    {
        var svc = CreateService();
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };

        bool passes = svc.PassesArtistMatchAcceptance(artist, "zzzzzzzz", user: null, artistIndex: null, out int score);

        Assert.False(passes);
        Assert.True(score < 60);
    }

    [Fact]
    public void Acceptance_PhoneticBranch_CodeCollisionFloored_Passes()
    {
        // The JF-381 live shape: "cup" heard for "Koop" (both Double Metaphone KP);
        // the phonetic overload floors the length-matched collision above the
        // containment score, so the gate accepts what the plain overload rejected.
        var svc = CreateService();
        var koop = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var codes = new Dictionary<Guid, (string Primary, string? Alternate)> { [koop.Id] = ("KP", null) };
        var index = new FakeArtistIndex(new[] { koop }, codes);

        bool passes = svc.PassesArtistMatchAcceptance(koop, "cup", user: null, artistIndex: index, out int score);

        Assert.True(passes);
        Assert.True(score >= 91);
    }

    [Fact]
    public void Acceptance_PhoneticBranch_LengthBandExcluded_ScoreZero_Rejected()
    {
        // A candidate far outside the matcher's length band (maxDiff = max(query*2, 15))
        // is never scored: the out param carries 0 for decision-point triage logs.
        var svc = CreateService();
        var artist = new MusicArtist { Name = "Dark Dark Dark Live", Id = Guid.NewGuid() };
        var index = new FakeArtistIndex(new[] { artist });

        bool passes = svc.PassesArtistMatchAcceptance(artist, "x", user: null, artistIndex: index, out int score);

        Assert.False(passes);
        Assert.Equal(0, score);
    }

    // ---------------------------------------------------------------------
    // ApplyAnnouncement (JF-345 the ONE override site)
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyAnnouncement_NullOrWhitespace_KeepsSpeech()
    {
        var response = ResponseBuilder.Tell("default speech");

        CrossMediaFallback.ApplyAnnouncement(response, null);
        Assert.Equal("default speech", ((PlainTextOutputSpeech)response.Response.OutputSpeech).Text);

        CrossMediaFallback.ApplyAnnouncement(response, "  ");
        Assert.Equal("default speech", ((PlainTextOutputSpeech)response.Response.OutputSpeech).Text);
    }

    [Fact]
    public void ApplyAnnouncement_NonEmpty_OverridesAsPlainText()
    {
        var response = ResponseBuilder.Tell("default speech");

        CrossMediaFallback.ApplyAnnouncement(response, "Playing something else");

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal("Playing something else", speech.Text);
    }

    // ---------------------------------------------------------------------
    // the composition seam
    // ---------------------------------------------------------------------

    [Fact]
    public void CrossMedia_Property_Wired_By_BaseHandler_Ctor()
    {
        var handler = new SharedGateProbeHandler(new Mock<ISessionManager>().Object, new PluginConfiguration(), _loggerFactory);

        Assert.NotNull(handler.CrossMedia);
        // The getter hands out ONE stable instance (not a fresh collaborator per
        // access); the ctor line above is what pins the per-handler wiring.
        Assert.Same(handler.CrossMedia, handler.CrossMedia);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private CrossMediaFallback CreateService(PluginConfiguration? config = null)
        => new(
            config ?? new PluginConfiguration(),
            _loggerFactory.CreateLogger<CrossMediaFallbackTests>(),
            new PlaybackLaunchBuilder(config ?? new PluginConfiguration(), _loggerFactory.CreateLogger<CrossMediaFallbackTests>(), (_, _, _) => Task.FromResult(false)),
            requestTimeoutMs: 6000);
}
