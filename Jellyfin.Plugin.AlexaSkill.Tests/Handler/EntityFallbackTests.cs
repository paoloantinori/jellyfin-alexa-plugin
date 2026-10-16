using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests the entity fallback logic wired into PlayMoodMusicIntentHandler:
/// when mood/genre search misses, the handler strips locale stop-words from
/// the slot text and tries it as an artist name via the phonetic search pipeline.
/// </summary>
[Collection("Plugin")]
public class EntityFallbackTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlayMoodMusicIntentHandler CreateHandler(IArtistIndex? artistIndex = null)
    {
        return new PlayMoodMusicIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            artistIndex: artistIndex);
    }

    private static IntentRequest CreateMoodIntent(string mood, string locale = "en-US")
    {
        var intent = new Intent { Name = IntentNames.PlayMoodMusic };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["mood"] = new Slot { Name = "mood", Value = mood }
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    /// <summary>
    /// Configures the library mock so genre/artist-genre searches return empty
    /// (triggering the entity fallback), and the artist SearchTerm query returns
    /// the given artist, followed by artist songs via ArtistIds.
    /// </summary>
    private void SetupArtistFallback(Guid artistId, string artistName, params Audio[] songs)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isArtistQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist);
                bool isAudioQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio);
                bool hasGenres = q.Genres != null && q.Genres.Count > 0;

                // Genre track search → empty
                if (hasGenres && isAudioQuery)
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem>();
                }

                // Artist-genre search → empty
                if (hasGenres && isArtistQuery)
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem>();
                }

                // Entity fallback: artist search via SearchTerm (no Genres)
                if (q.SearchTerm != null && isArtistQuery && !hasGenres)
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem> { new MusicArtist { Name = artistName, Id = artistId } };
                }

                // Artist songs (ArtistIds + MediaTypes Audio)
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return (IReadOnlyList<BaseItem>)new List<BaseItem>(songs);
                }

                return (IReadOnlyList<BaseItem>)new List<BaseItem>();
            });
    }

    [Fact]
    public async Task ItalianMood_DiMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("di miles davis", "it-IT");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task EnglishMood_ByMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("by miles davis", "en-US");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task GermanMood_VonMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("von miles davis", "de-DE");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task NoMatch_ReturnsNull_CallerShowsNotFoundMood()
    {
        _fx.SetupUserMock();

        // All searches return empty — no genre tracks, no artist-genre, no artist fallback
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var request = CreateMoodIntent("relaxing", "en-US");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // No directives — NotFoundMood tell
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("relaxing", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpanishMood_DeMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("de miles davis", "es-ES");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task PortugueseMood_DoMilesDavis_PlaysArtist()
    {
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("do miles davis", "pt-BR");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Miles Davis", speech);
    }

    [Fact]
    public async Task MaxWordsGuard_LongMood_DoesNotFallbackToArtist()
    {
        // An artist IS available, but a >CrossMediaArtistMaxWords-word slot is a poor artist
        // query — the guard must skip the fallback (a wrong-artist false positive is worse
        // than a clean not-found). Same guard PlaySong's cross-media fallback uses.
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "So What", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupArtistFallback(artistId, "Miles Davis", song);

        var handler = CreateHandler();
        var request = CreateMoodIntent("miles davis john coltrane quartet", "en-US");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // No AudioPlayer.Play — the guard prevented the artist fallback despite the artist being available.
        Assert.True(response.Response?.Directives == null || response.Response.Directives.Count == 0);
    }

    // ====================================================================
    // JF-894: containment-tie arbitration in the shared entity-fallback gate.
    // Live incident (corr 72eca45c, 2026-10-16): 'musica si nora jones' degraded
    // by the NLU to mood='jones'; the strict bar accepted the FIRST 90-scoring
    // containment tie (Danko Jones) and auto-played it while Norah Jones scored
    // the same 90. The JF-420 discipline (fair scores over every rival) must ask.
    // ====================================================================

    /// <summary>
    /// Configures the library mock for the index-backed pins: every genre search
    /// misses (triggering the entity fallback) and the artist-songs fetch returns
    /// the given songs. The artist search itself runs in memory over the
    /// <see cref="FakeArtistIndex"/> the handler was built with. Pass
    /// <paramref name="restrictSongsToArtist"/> to answer the songs fetch ONLY for
    /// that artist id, so a pin can prove WHICH artist the gate selected (a wrong
    /// pick lands the NoSongsForArtist tell instead of playback).
    /// </summary>
    private void SetupGenreMissWithArtistSongs(Audio[] songs, Guid? restrictSongsToArtist = null)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.Genres != null && q.Genres.Count > 0)
                {
                    return new List<BaseItem>();
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0
                    && (restrictSongsToArtist == null || q.ArtistIds[0].Equals(restrictSongsToArtist)))
                {
                    return new List<BaseItem>(songs);
                }

                return new List<BaseItem>();
            });
    }

    private static bool HasAudioPlayerDirective(SkillResponse response)
        => TestHelpers.GetPlayDirective(response) != null;

    [Fact]
    public async Task JF894_TiedContainmentMatch_AsksWithBothCandidates_NeverAutoPicks()
    {
        // THE regression pin. The pool order puts Danko Jones FIRST so the
        // best-match early exit picks it as the winner (the live incident's
        // arbitrary pick); Norah Jones ties at the same containment score.
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var norah = new MusicArtist { Name = "Norah Jones", Id = Guid.NewGuid() };
        var control = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Come Away with Me", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithArtistSongs(new[] { song });
        var index = new FakeArtistIndex(new[] { danko, norah, control });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("jones");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        // The ask fires: never an arbitrary auto-pick of either tied artist.
        Assert.False(HasAudioPlayerDirective(response));
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Danko Jones", speech);
        Assert.Contains("Norah Jones", speech);
        // The JF-420.2 disambiguation state: "yes" plays the strongest candidate.
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes?[DisambiguationHelper.AttrType]);
        string matches = response.SessionAttributes?[DisambiguationHelper.AttrMatches]?.ToString() ?? string.Empty;
        Assert.Contains("Danko Jones", matches);
        Assert.Contains("Norah Jones", matches);
    }

    [Fact]
    public async Task JF894_UniqueContainmentMatch_StillAutoPlaysWithFoundArtistInstead()
    {
        // 'danko' matches only Danko Jones at the containment bar: a unique
        // match at >= threshold keeps the existing auto-play (no ask).
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var norah = new MusicArtist { Name = "Norah Jones", Id = Guid.NewGuid() };
        var control = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Had Enough", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithArtistSongs(new[] { song });
        var index = new FakeArtistIndex(new[] { danko, norah, control });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("danko");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(HasAudioPlayerDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Danko Jones", speech);
    }

    [Fact]
    public async Task JF894_ExactFullName_PlaysDirectly()
    {
        // JF-420.1: the full spoken name exact-matches one artist; the
        // arbitration is bypassed and the artist plays.
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var norah = new MusicArtist { Name = "Norah Jones", Id = Guid.NewGuid() };
        var control = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Don't Know Why", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithArtistSongs(new[] { song });
        var index = new FakeArtistIndex(new[] { norah, danko, control });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("norah jones");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(HasAudioPlayerDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Norah Jones", speech);
        Assert.DoesNotContain("Danko Jones", speech);
    }

    [Fact]
    public async Task JF894_FourWayTie_KeepsEveryTiedRivalReachable_SpeaksCapped()
    {
        // The library carries four Jones artists: the ask's cycling STATE keeps
        // all of them (DisambiguateNext reaches rank 4) while the spoken list is
        // capped at MultipleArtistsSpeakCap (3) with the total leading the
        // sentence (JF-880).
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var norah = new MusicArtist { Name = "Norah Jones", Id = Guid.NewGuid() };
        var tom = new MusicArtist { Name = "Tom Jones", Id = Guid.NewGuid() };
        var quincy = new MusicArtist { Name = "Quincy Jones", Id = Guid.NewGuid() };
        var control = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var song = new Audio { Name = "It's Not Unusual", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithArtistSongs(new[] { song });
        var index = new FakeArtistIndex(new[] { danko, norah, tom, quincy, control });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("jones");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.False(HasAudioPlayerDirective(response));
        string speech = TestHelpers.GetSpeechText(response);
        // The three strongest fair scores are spoken; the fourth stays reachable
        // only through the yes/no cycling state.
        Assert.Contains("Tom Jones", speech);
        Assert.Contains("Norah Jones", speech);
        Assert.Contains("Danko Jones", speech);
        Assert.DoesNotContain("Quincy Jones", speech);
        string matches = response.SessionAttributes?[DisambiguationHelper.AttrMatches]?.ToString() ?? string.Empty;
        Assert.Contains("Quincy Jones", matches);
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes?[DisambiguationHelper.AttrType]);
    }

    [Fact]
    public async Task JF894_ClearFairWinner_PlaysWithoutAsking()
    {
        // The mirror dominance leg: 'Jonesy' is within one character of the
        // query's length while 'Danko Jones' is a distant containment, so the
        // fair comparison says the winner beats every rival by more than the
        // JF-420 margin. No artist sits within the margin: no tie, no ask, the
        // winner plays.
        var jonesy = new MusicArtist { Name = "Jonesy", Id = Guid.NewGuid() };
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Hey Jonesy", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithArtistSongs(new[] { song });
        var index = new FakeArtistIndex(new[] { jonesy, danko });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("jones");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(HasAudioPlayerDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jonesy", speech);
    }

    [Fact]
    public async Task JF894_ExactNamedArtistWinsCollapse_PlaysThroughTheBypass()
    {
        // JF-895 correction of this pin's history: the exact-named 'Jones' wins the
        // single-best collapse OUTRIGHT (the JF-892 exact-first reorder puts it ahead
        // of the containment rivals, and the collapse returns it at 100), so the gate
        // returns null at the JF-420.1 equality bypass and Jones plays. The
        // auto-select swap arm is NOT what fires here; the genuine firing fixture is
        // JF895_DominantRivalAutoSelects_NonExactWinner_SwapFires below. This pin
        // locks the bypass OUTCOME (exact winner plays, no ask) on the same shape;
        // the mirror-dominance leg returns the same null for it, so the bypass
        // MECHANISM is only pinned indirectly.
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var norah = new MusicArtist { Name = "Norah Jones", Id = Guid.NewGuid() };
        var jones = new MusicArtist { Name = "Jones", Id = Guid.NewGuid() };
        var songForJones = new Audio { Name = "The Jones Song", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        // The artist-songs fetch answers ONLY for the exact-named artist, so a
        // failure to swap the selection lands the NoSongsForArtist tell (no
        // directives) instead of playback.
        SetupGenreMissWithArtistSongs(new[] { songForJones }, restrictSongsToArtist: jones.Id);

        var index = new FakeArtistIndex(new[] { danko, norah, jones });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("jones");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(HasAudioPlayerDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(songForJones.Id, session.NowPlayingQueue![0].Id);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jones", speech);
    }

    [Fact]
    public async Task JF895_DominantRivalAutoSelects_NonExactWinner_SwapFires()
    {
        // THE genuine auto-select-swap fixture (JF-895): the collapse winner must be
        // NON-exact, so the JF-420.1 bypass cannot answer first. 'Jonas' never enters
        // the chain results (the tier-1 Contains filter for 'jones' skips it) but IS
        // in the tie gate's rival pool (the full scoped index fetch), where the
        // phonetic scorer rates it far above the winner's containment fair score
        // (90*5/11 = 40.9), clearing both AlternativeFullNameThreshold and the JF-420
        // margin: ResolveContainmentTie's swap arm runs and the selection moves to
        // Jonas before playback.
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var norah = new MusicArtist { Name = "Norah Jones", Id = Guid.NewGuid() };
        var jonas = new MusicArtist { Name = "Jonas", Id = Guid.NewGuid() };
        var songForJonas = new Audio { Name = "Molo", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        // The songs fetch answers ONLY for Jonas, so a failure to swap lands the
        // NoSongsForArtist tell (no directives) instead of playback.
        SetupGenreMissWithArtistSongs(new[] { songForJonas }, restrictSongsToArtist: jonas.Id);

        var index = new FakeArtistIndex(new[] { danko, norah, jonas });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("jones");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(HasAudioPlayerDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(songForJonas.Id, session.NowPlayingQueue![0].Id);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Jonas", speech);
    }

    [Fact]
    public async Task JF895_Twin_WeakRivalBelowTheFairBar_FallsBackToTheAsk()
    {
        // The twin that proves the swap above is the MARGIN machinery firing, not
        // merely a rival's presence: 'Jonasine' is the same phonetic rival shape
        // (JNS collision, absent from the chain's Contains results, present in the
        // pool), but its 8-character name caps the fair score at raw*5/8 <= 62.5,
        // below AlternativeFullNameThreshold (80) no matter how the phonetic scorer
        // rates it. With no eligible dominant rival the gate falls through to the
        // JF-420.2 ask among the tied candidates (Jonasine stays in the ask list, its
        // sub-bar fair score is no ask filter), never an auto-play of any of them.
        // The Jonasine speech assert below also fails loud if a scorer-calibration
        // change drops it below the rival bar, so this pin cannot silently degrade
        // into a duplicate of JF894_TiedContainmentMatch.
        var danko = new MusicArtist { Name = "Danko Jones", Id = Guid.NewGuid() };
        var norah = new MusicArtist { Name = "Norah Jones", Id = Guid.NewGuid() };
        var jonasine = new MusicArtist { Name = "Jonasine", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Her Name Is Jonasine", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        // Symmetric with the swap fixture: the songs fetch answers ONLY for
        // Jonasine, so an erroneous auto-play of ANY of the three artists fails.
        SetupGenreMissWithArtistSongs(new[] { song }, restrictSongsToArtist: jonasine.Id);

        var index = new FakeArtistIndex(new[] { danko, norah, jonasine });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("jones");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.False(HasAudioPlayerDirective(response));
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes?[DisambiguationHelper.AttrType]);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Danko Jones", speech);
        Assert.Contains("Norah Jones", speech);
        Assert.Contains("Jonasine", speech);
    }

    [Fact]
    public async Task JF894_PhoneticFloorTie_BothRivalsVisible_Asks()
    {
        // The rival scan scores through the shared PHONETIC scorer: 'Koop' and
        // 'Koup' both Double-Metaphone KP and both floor at 91 for the heard
        // 'cup', but plain scoring leaves 'Koup' at ~66 (below the bar) where
        // the arbitrary pick the gate exists to remove would survive.
        var koop = new MusicArtist { Name = "Koop", Id = Guid.NewGuid() };
        var koup = new MusicArtist { Name = "Koup", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Waltz for Koop", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithArtistSongs(new[] { song });
        var index = new FakeArtistIndex(
            new[] { koop, koup },
            FakeArtistIndex.CodesFromArtistNames(koop, koup));

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("cup");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.False(HasAudioPlayerDirective(response));
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Koop", speech);
        Assert.Contains("Koup", speech);
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes?[DisambiguationHelper.AttrType]);
    }

    [Fact]
    public async Task JF894_DuplicateLibraryRowsOfOneArtist_PlayWithoutAsking()
    {
        // Two library rows of the same artist (the same name in two libraries)
        // are one choice for the user: the name dedup (the JF-416 precedent)
        // keeps them out of the ask, so the winner plays instead of cycling
        // through indistinguishable entries.
        var beatlesA = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var beatlesB = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var control = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Abbey Road Medley", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithArtistSongs(new[] { song });
        var index = new FakeArtistIndex(new[] { beatlesA, beatlesB, control });

        var handler = CreateHandler(index);
        var request = CreateMoodIntent("beatles");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(HasAudioPlayerDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("The Beatles", speech);
    }

    [Fact]
    public async Task JF896_CoincidentalWinner_ShadowingQualifiedNonExactRival_AsksWithBoth()
    {
        // THE JF-896 pin (the JF-776 refuse-and-continue walk, adopted): for
        // 'goldbug stories' the chain's first-90 winner 'Bug' is a coincidental
        // containment, and a plausible NON-exact rival ('Goldbug Story', raw
        // ~92 / fair ~80 vs the winner's fair 18) sits beside it in the
        // results. JF-892's exact-first reorder rescues only the EXACT
        // subclass, so before JF-896 this shape answered the clean not-found
        // and buried the rival (the JF-894 residual, pinned green
        // deliberately). The honest landing is the JF-420.2 multi-artist ask
        // naming BOTH candidates: never a silent auto-play (the JF-377
        // string-indistinguishable lesson: the ASK is the only no-regression
        // landing) and never the clean not-found. History note: the old
        // downgrade-outranks-tie-gate ORDER pin lived here; its mechanism
        // survives unchanged (the JF-382 downgrade still owns the coincidental
        // winner), only the LANDING upgrades when the rival pool holds a
        // qualified candidate.
        var bug = new MusicArtist { Name = "Bug", Id = Guid.NewGuid() };
        var goldbug = new MusicArtist { Name = "Goldbug Story", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        // Songs never fetched (the ask answers before playback), so the
        // never-matching songs id keeps the songs leg dead.
        SetupGenreMissWithDbArtistRows(new BaseItem[] { bug, goldbug }, Guid.Empty, Array.Empty<Audio>());

        var handler = CreateHandler();
        var request = CreateMoodIntent("goldbug stories");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        // The JF-420.2 ask, not the clean mood not-found and not a silent
        // 'Goldbug Story' auto-select.
        Assert.False(response.Response?.ShouldEndSession);
        Assert.False(HasAudioPlayerDirective(response));
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Goldbug Story", speech);
        Assert.Contains("Bug", speech);
        // Fair-descending order (the JF-894 ask contract): the qualified rival
        // headlines so "yes" plays the strongest candidate, never the coincidental
        // winner the walk exists to demote (it enters at its FAIR score, ~18, not
        // the raw containment floor).
        Assert.True(
            speech.IndexOf("Goldbug Story", StringComparison.Ordinal) < speech.IndexOf("Bug", StringComparison.Ordinal),
            $"expected the rival to head the ask, got: {speech}");
        string matchesAttr = response.SessionAttributes?[DisambiguationHelper.AttrMatches]?.ToString() ?? string.Empty;
        Assert.True(
            matchesAttr.IndexOf("Goldbug Story", StringComparison.Ordinal) < matchesAttr.IndexOf("Bug", StringComparison.Ordinal),
            $"expected the rival to rank first in the cycling state, got: {matchesAttr}");
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, response.SessionAttributes?[DisambiguationHelper.AttrType]);
    }

    [Fact]
    public async Task JF896_CoincidentalWinner_RivalUnqualified_StillCleanNotFound()
    {
        // The no-false-positive side: the coincidental winner is demoted and
        // the rival pool holds NO candidate that clears the JF-420 alternative
        // bar ('Radiohead' barely rates against 'goldbug stories'), so the
        // JF-382 downgrade keeps its clean-miss landing, byte-identical to
        // pre-JF-896. A weak rival must never buy an ask.
        var bug = new MusicArtist { Name = "Bug", Id = Guid.NewGuid() };
        var weak = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithDbArtistRows(new BaseItem[] { bug, weak }, Guid.Empty, Array.Empty<Audio>());

        var handler = CreateHandler();
        var request = CreateMoodIntent("goldbug stories");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession);
        Assert.False(HasAudioPlayerDirective(response));
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("goldbug stories", speech, StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================
    // JF-892: the DB tier-1 SearchTerm rows are unordered, so a coincidental
    // containment candidate listed before the exact-named artist could win
    // the fallback's single-best collapse (the JF-776 B1 shadowing class).
    // ====================================================================

    /// <summary>
    /// Configures the library mock for the JF-892 DB-path shape: every genre
    /// search misses (triggering the entity fallback), the artist SearchTerm
    /// query returns the given rows in the given order, and the artist-songs
    /// fetch answers ONLY for <paramref name="songsArtistId"/> (a wrong pick
    /// lands the NoSongsForArtist tell instead of playback, so the assertion
    /// proves WHICH artist the gate selected).
    /// </summary>
    private void SetupGenreMissWithDbArtistRows(BaseItem[] artistRows, Guid songsArtistId, Audio[] songs)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.Genres != null && q.Genres.Count > 0)
                {
                    return new List<BaseItem>();
                }

                // Artist search via SearchTerm (the DB path), rows in the given
                // order (GetItemList is unordered in production; the order is
                // the load-bearing part of the shadow shape).
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem>(artistRows);
                }

                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.ArtistIds[0].Equals(songsArtistId))
                {
                    return new List<BaseItem>(songs);
                }

                return new List<BaseItem>();
            });
    }

    [Fact]
    public async Task JF892_ExactArtistShadowedByEarlierCoincidentalDbRow_PlaysExact()
    {
        // THE red test: rows [Bug, Goldbug Stories] for query 'goldbug
        // stories'. The coincidental containment candidate is listed first, so
        // the collapse's early exit fired on 'Bug' at the containment score
        // and the exact-named artist was never scored; the JF-382 downgrade
        // then demoted 'Bug' and the mood path answered the clean not-found.
        // With exact-first ordering the exact artist is scored first (100) and
        // plays: the JF-382 downgrade cannot demote an exact name and the
        // JF-894 tie gate bypasses on exact-name equality (JF-420.1).
        var bug = new MusicArtist { Name = "Bug", Id = Guid.NewGuid() };
        var goldbug = new MusicArtist { Name = "Goldbug Stories", Id = Guid.NewGuid() };
        var song = new Audio { Name = "The Goldbug Song", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithDbArtistRows(new BaseItem[] { bug, goldbug }, goldbug.Id, new[] { song });

        var handler = CreateHandler();
        var request = CreateMoodIntent("goldbug stories");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(HasAudioPlayerDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue![0].Id);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Goldbug Stories", speech);
    }

    [Fact]
    public async Task JF892_CoincidentalOnlyNoExactArtist_StillCleanNotFound()
    {
        // The no-false-positive side: the exact-named artist is NOT in the
        // library (only the coincidental 'Bug' row), so the JF-382 downgrade
        // keeps demoting the winner and the no-offer mood path answers the
        // clean not-found, exactly as before JF-892.
        var bug = new MusicArtist { Name = "Bug", Id = Guid.NewGuid() };
        var song = new Audio { Name = "Bug Sound", Id = Guid.NewGuid() };

        _fx.SetupUserMock();
        SetupGenreMissWithDbArtistRows(new BaseItem[] { bug }, bug.Id, new[] { song });

        var handler = CreateHandler();
        var request = CreateMoodIntent("goldbug stories");
        var context = _fx.CreateContext();
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, _fx.CreateUser(), session, CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession);
        Assert.False(HasAudioPlayerDirective(response));
        Assert.True(session.NowPlayingQueue == null || session.NowPlayingQueue.Count == 0);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("goldbug stories", speech, StringComparison.OrdinalIgnoreCase);
    }
}
