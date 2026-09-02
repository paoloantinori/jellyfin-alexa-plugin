using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayArtistSongsIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly Mock<IArtistIndex> _artistIndexMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayArtistSongsIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _artistIndexMock = new Mock<IArtistIndex>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayArtistSongsIntentHandler CreateHandler(IArtistIndex? artistIndex = null, ISongNgramIndex? songNgramIndex = null)
    {
        return new PlayArtistSongsIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory,
            artistIndex,
            songNgramIndex);
    }

    private static IntentRequest CreateIntentRequest(string? musician = null)
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (musician != null)
        {
            intent.Slots["musician"] = new global::Alexa.NET.Request.Slot { Name = "musician", Value = musician };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private void SetupSongResult(params Audio[] songs)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(songs.ToList<BaseItem>());
    }

    // --- JF-420.3: symmetric fair-score margin + matcher-aligned penalty ---

    /// <summary>
    /// JF-437 live finding (minix, 2026-09-01): 'beatles live' resolved to Eagles
    /// because the intended artist is neither a contiguous substring (tier 1) nor a
    /// prefix (tiers 2-3) of the query, and tier-4's partial window ranks the
    /// near-anagram 'Eagles' (83, via 'eatles' at edit distance 1) above 'The
    /// Beatles' (27, the article misaligns every window). The word-coverage tier
    /// must surface The Beatles ({beatles} covers the query's first word) instead.
    /// </summary>
    [Fact]
    public async Task HandleAsync_BeatlesLiveQuery_PlaysTheBeatles_NeverEagles()
    {
        var theBeatles = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var eagles = new MusicArtist { Name = "Eagles", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { theBeatles, eagles };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        var beatlesSong = new Audio { Name = "Yesterday", Id = Guid.NewGuid() };
        var eaglesSong = new Audio { Name = "Hotel California", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) => q.ArtistIds.Contains(theBeatles.Id)
                ? new List<BaseItem> { beatlesSong }
                : new List<BaseItem> { eaglesSong });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "beatles live");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession, "must auto-play, not prompt");
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        var metadata = ((AudioPlayerPlayDirective)play).AudioItem?.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("Yesterday", metadata.Title); // The Beatles's song, never Eagles's
    }


    /// <summary>
    /// JF-420.3 scenario: query 'miles davis live' with 'Miles Davis' and 'Miles' in
    /// the library. Whichever of the two the tier-2 prefix match surfaces first, the
    /// outcome must be Miles Davis (word-subset skip when the alternative adds
    /// nothing; symmetric fair scores when it does), never an auto-select of 'Miles'.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MilesDavisLiveQuery_PlaysMilesDavis_NeverMiles()
    {
        var milesDavis = new MusicArtist { Name = "Miles Davis", Id = Guid.NewGuid() };
        var miles = new MusicArtist { Name = "Miles", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { milesDavis, miles };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        var davisSong = new Audio { Name = "So What", Id = Guid.NewGuid() };
        var milesSong = new Audio { Name = "Blue Moods", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) => q.ArtistIds.Contains(milesDavis.Id)
                ? new List<BaseItem> { davisSong }
                : new List<BaseItem> { milesSong });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "miles davis live");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession, "must auto-play, not prompt");
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        var metadata = ((AudioPlayerPlayDirective)play).AudioItem?.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("So What", metadata.Title); // Miles Davis's song, not Miles's
    }

    /// <summary>
    /// JF-437 review round: tier 1.5 runs AFTER tier 2, so ASR drift still resolves
    /// through the fuzzy/phonetic tier - 'soul coughin' must play Soul Coughing, not
    /// the one-word word-subset artist 'Soul' that the predicate alone would return.
    /// </summary>
    [Fact]
    public async Task HandleAsync_SoulCoughinDrift_Tier2ResolvesBeforeWordCoverage()
    {
        var soul = new MusicArtist { Name = "Soul", Id = Guid.NewGuid() };
        var soulCoughing = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { soul, soulCoughing };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        var coughingSong = new Audio { Name = "Sugar Free Jazz", Id = Guid.NewGuid() };
        var soulSong = new Audio { Name = "Soul Song", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) => q.ArtistIds.Contains(soulCoughing.Id)
                ? new List<BaseItem> { coughingSong }
                : new List<BaseItem> { soulSong });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "soul coughin");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        var metadata = ((AudioPlayerPlayDirective)play).AudioItem?.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("Sugar Free Jazz", metadata.Title); // Soul Coughing's song, not Soul's
    }

    // --- JF-439: inverse cross-media fallback (artist not-found -> song search) ---

    /// <summary>
    /// The motivating case: no artist named "sugar free jazz" exists, the musician
    /// slot carries a multi-word musician-shaped SONG title (NLU coin flip, JF-438),
    /// the song index has it. Must play the song with the FoundSongInstead
    /// announcement, not answer NotFoundArtist.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ArtistMiss_MusicianShapedSongTitle_PlaysSongWithAnnouncement()
    {
        var noArtists = new List<BaseItem>();
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(noArtists);

        var song = new Audio { Name = "Sugar Free Jazz", Id = Guid.NewGuid() };
        var songIndex = new TestHelpers.FakeSongIndex((song, 105));

        var handler = CreateHandler(_artistIndexMock.Object, songIndex);
        var request = CreateIntentRequest(musician: "sugar free jazz");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        var speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Sugar Free Jazz", speech, StringComparison.OrdinalIgnoreCase); // announcement names the song
    }

    /// <summary>
    /// Review round: the score bar replaces the word-count guard (a spaceless CJK
    /// title is one token). A phonetic half-coverage hit ("rolling stones" ~&gt;
    /// "Like a Rolling Stone", score ~34) must NOT substitute an unrelated song:
    /// the clean NotFoundArtist stands.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ArtistMiss_LowScoreSongCandidate_KeepsCleanNotFound()
    {
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(new List<BaseItem>());

        var song = new Audio { Name = "Like a Rolling Stone", Id = Guid.NewGuid() };
        var handler = CreateHandler(_artistIndexMock.Object, new TestHelpers.FakeSongIndex((song, 34))); // phonetic half-coverage score
        var request = CreateIntentRequest(musician: "rolling stones");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        var speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("rolling stones", speech, StringComparison.OrdinalIgnoreCase); // NotFoundArtist wording
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play"));
    }

    /// <summary>
    /// No artist AND no song match: the clean NotFoundArtist is unchanged.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ArtistMiss_SongMiss_CleanNotFound()
    {
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(new List<BaseItem>());

        var handler = CreateHandler(_artistIndexMock.Object, new TestHelpers.FakeSongIndex());
        var request = CreateIntentRequest(musician: "blue marble dreams");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        var speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("blue marble dreams", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play"));
    }

    /// <summary>
    /// A warming song index must not convert the not-found into a warming Tell: the
    /// fallback catches the gate's exception and degrades to NotFoundArtist.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ArtistMiss_WarmingSongIndex_CleanNotFound()
    {
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(new List<BaseItem>());

        var warming = new Mock<ISongNgramIndex>();
        warming.Setup(i => i.IsReady).Returns(false);
        warming.Setup(i => i.IsDisabled).Returns(false);
        warming.Setup(i => i.Search(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<Guid[]?>()))
            .Throws(new SkillWarmingUpException("song n-gram"));

        var handler = CreateHandler(_artistIndexMock.Object, warming.Object);
        var request = CreateIntentRequest(musician: "sugar free jazz");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        var speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("sugar free jazz", speech, StringComparison.OrdinalIgnoreCase); // NotFoundArtist, not SkillWarmingUp
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play"));
    }

    /// <summary>
    /// JF-420.2: the multi-candidate prompt speaks the interaction the state machine
    /// actually supports (yes plays the first, no cycles to DisambiguateNext): no
    /// numbered list inviting a numeric answer nothing consumes, and the session
    /// attributes are written through DisambiguationHelper's constants.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MultiCandidatePrompt_SpeaksYesNoContractWithoutNumbering()
    {
        var beatles = new MusicArtist { Name = "Beatles", Id = Guid.NewGuid() };
        var live = new MusicArtist { Name = "Live", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { beatles, live };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        SetupSongResult(new Audio { Name = "Yesterday", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "beatles live");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession, "ambiguous case must prompt");
        var speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("1.", speech, StringComparison.Ordinal); // no numbering
        Assert.DoesNotContain("2.", speech, StringComparison.Ordinal);
        Assert.Contains("Beatles", speech, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Live", speech, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey(DisambiguationHelper.AttrMatches));
        Assert.Equal(0, response.SessionAttributes[DisambiguationHelper.AttrIndex]);
    }

    /// <summary>
    /// JF-420.3 review round 2 (early-exit masking): the gate must rank ALL
    /// alternatives by fair score. FindBestMatchWithScore returned on the FIRST
    /// candidate reaching 90, so a containment-exempt 'Floyd' earlier in index order
    /// masked 'Pink Floyd' entirely; with the full ranking the fair scores are
    /// Floyd 45 vs Pink Floyd 90 and Pink Floyd is auto-selected.
    /// </summary>
    [Fact]
    public async Task HandleAsync_PnkFloydWithFloydInLibrary_AutoSelectsPinkFloyd()
    {
        var pnk = new MusicArtist { Name = "P!nk", Id = Guid.NewGuid() };
        var floyd = new MusicArtist { Name = "Floyd", Id = Guid.NewGuid() };
        var pinkFloyd = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { pnk, floyd, pinkFloyd };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        var pinkFloydSong = new Audio { Name = "Comfortably Numb", Id = Guid.NewGuid() };
        var floydSong = new Audio { Name = "Floyd Song", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns((InternalItemsQuery q) => q.ArtistIds.Contains(pinkFloyd.Id)
                ? new List<BaseItem> { pinkFloydSong }
                : new List<BaseItem> { floydSong });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "P!nk floyd");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession, "must auto-play, not prompt");
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        var metadata = ((AudioPlayerPlayDirective)play).AudioItem?.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("Comfortably Numb", metadata.Title); // Pink Floyd's song, never Floyd's
    }

    /// <summary>
    /// JF-420.3 phantom-margin regression, in the reachable gate shape: query
    /// 'beatles live' surfaces 'Beatles' as the single containment match (tier 2,
    /// ratio 7/11 = 0.636, above ApplyLengthPenalty's 0.5 floor) while an artist
    /// literally named 'Live' scores 90 via the containment exemption. The handler's
    /// old fair-score (90 * 7/11 = 57) disagreed with the matcher's floor (90) by 33
    /// points: a phantom margin that auto-selected 'Live'. With both sides scored by
    /// the matcher's own semantics the alternative's fair score is 32 (below the
    /// 80 bar: exemption-only hits cannot win) and the gate disambiguates instead.
    /// </summary>
    [Fact]
    public async Task HandleAsync_BeatlesLiveQuery_FairScoreAligned_DisambiguatesInsteadOfShortAlternative()
    {
        var beatles = new MusicArtist { Name = "Beatles", Id = Guid.NewGuid() };
        var live = new MusicArtist { Name = "Live", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { beatles, live };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        SetupSongResult(new Audio { Name = "Yesterday", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "beatles live");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession, "phantom-margin case must disambiguate, not auto-select 'Live'");
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play"));
    }

    [Fact]
    public void CanHandle_PlayArtistSongsIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Beatles");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_WithInMemoryIndex_FindsArtist()
    {
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { artist };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);

        SetupUserMock();
        SetupSongResult(
            new Audio { Name = "Yesterday", Id = Guid.NewGuid() },
            new Audio { Name = "Let It Be", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        // Verify the index was queried, not the DB
        _artistIndexMock.Verify(i => i.GetArtists(It.IsAny<Guid[]?>()), Times.Once);
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == Jellyfin.Data.Enums.BaseItemKind.MusicArtist))), Times.Never);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_ContainmentMatchWithFullNameAlternative_AutoSelectsAlternative()
    {
        // JF-420 user-driven improvement: "P!nk floyd" should auto-select Pink Floyd,
        // not ask the user to choose (they already said "pink floyd" once). The fair
        // comparison: P!nk's penalized score is 90 * 4/10 = 36, Pink Floyd's genuine
        // score is ~90. The margin (54 > 20) makes it unambiguous.
        var pnk = new MusicArtist { Name = "P!nk", Id = Guid.NewGuid() };
        var pinkFloyd = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { pnk, pinkFloyd };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        SetupUserMock();
        SetupSongResult(new Audio { Name = "Comfortably Numb", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "P!nk floyd");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Auto-play Pink Floyd (no disambiguation prompt)
        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        var metadata = ((AudioPlayerPlayDirective)play).AudioItem?.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("Comfortably Numb", metadata.Title);
    }

    [Fact]
    public async Task HandleAsync_ContainmentMatchNoAlternative_AutoPlays()
    {
        // JF-420 no-regression: "nirvana unplugged" with only Nirvana (no
        // full-name alternative above 80) auto-plays without prompting.
        var nirvana = new MusicArtist { Name = "Nirvana", Id = Guid.NewGuid() };
        var radiohead = new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { nirvana, radiohead };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        SetupUserMock();
        SetupSongResult(new Audio { Name = "Smells Like Teen Spirit", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "nirvana unplugged");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Auto-play: no disambiguation
        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
    }

    [Fact]
    public async Task HandleAsync_ExactMatchWithContainmentCandidate_AutoPlays()
    {
        // JF-420.1 regression: exact multi-word query with a longer containment artist
        // in the library ("Pink Floyd" + "The Pink Floyd Tribute Band"). Tier 1 returns
        // only the exact match (the tribute band is JF-381 band-gated); the JF-420 gate
        // must NOT demote an exact match to a disambiguation prompt.
        var pinkFloyd = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        var tribute = new MusicArtist { Name = "The Pink Floyd Tribute Band", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { pinkFloyd, tribute };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        SetupUserMock();
        SetupSongResult(new Audio { Name = "Comfortably Numb", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "pink floyd");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Exact match auto-plays: no disambiguation prompt
        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        var metadata = ((AudioPlayerPlayDirective)play).AudioItem?.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("Comfortably Numb", metadata.Title);
    }

    [Fact]
    public async Task HandleAsync_ExactMatchWithCollaborationSuffix_AutoPlays()
    {
        // JF-420.1 live evidence (2026-08-31): the library gained
        // "Soul Coughing & Roni Size"; the exact query "Soul Coughing" started
        // prompting disambiguation instead of auto-playing. Reproduced at unit level
        // (the live simulator test test_exact_artist_name_still_works).
        var soulCoughing = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var collaboration = new MusicArtist { Name = "Soul Coughing & Roni Size", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { soulCoughing, collaboration };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);
        SetupUserMock();
        SetupSongResult(new Audio { Name = "Circles", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Soul Coughing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var play = response.Response.Directives?.FirstOrDefault(d => d.Type == "AudioPlayer.Play");
        Assert.NotNull(play);
        // The EXACT artist plays (Soul Coughing), not the collaboration
        var metadata = ((AudioPlayerPlayDirective)play).AudioItem?.Metadata;
        Assert.NotNull(metadata);
        Assert.Equal("Circles", metadata.Title);
    }

    [Fact]
    public async Task HandleAsync_IndexNotReady_ThrowsWarmingException()
    {
        // JF-419.2 UPDATED: when the artist index EXISTS but IsReady is false (cold-start
        // after DLL deploy), the shared choke point throws SkillWarmingUpException (the
        // request pipeline translates it into the warming Tell; the user-facing behavior
        // is asserted in SkillWarmingUpTests). The DB fall-through only happens when the
        // index is NULL (no index service configured, e.g. minimal test setups).
        _artistIndexMock.Setup(i => i.IsReady).Returns(false);

        SetupUserMock();

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(request, context, user, session, CancellationToken.None));

        // The throw fires before any search: the DB is never queried
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ArtistSongsQuery_FiltersByIncludeItemTypesAudio()
    {
        // JF-358: the artist-songs query must filter with IncludeItemTypes=Audio, NOT
        // MediaTypes=Audio. On Jellyfin 10.11.11, MediaTypes=Audio does not constrain an
        // ArtistIds query (returns the entire audio library), which makes the sort run over
        // thousands of items and intermittently NRE inside UserDataManager.GetUserData ->
        // RetryAsync burns the 8s Alexa budget -> INVALID_RESPONSE. IncludeItemTypes filters
        // correctly and returns only the artist's songs.
        SetupUserMock();
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { new Audio { Name = "Yesterday", Id = Guid.NewGuid() } };
            });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "Beatles");

        await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        // The SECOND GetItemList call is the artist-songs query (the first resolves the artist).
        // It must request Audio via IncludeItemTypes, and must NOT rely on MediaTypes alone.
        _libraryManagerMock.Verify(
            l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ArtistIds != null && q.ArtistIds.Length > 0
                && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Audio))),
            Times.AtLeastOnce,
            "artist-songs query must filter via IncludeItemTypes=Audio (JF-358: MediaTypes=Audio does not filter ArtistIds queries on Jellyfin 10.11.11)");
    }

    [Fact]
    public async Task HandleAsync_ColdDbPath_ArtistQueriesSetIncludeItemsByName()
    {
        // JF-456 (GH #22 residual 3): folderless artists carry NULL TopParentId, so a
        // library-restricted DB artist query matches zero rows unless IncludeItemsByName
        // activates the items-by-name bypass. Pins the INLINE DB tiers (the cold-index
        // path this handler keeps, JF-382 duplication) the same way ArtistSearchTests
        // pins the shared implementation.
        SetupUserMock();
        var libraryId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { libraryId.ToString() });
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { new Audio { Name = "Yesterday", Id = Guid.NewGuid() } };
            });

        var handler = CreateHandler(artistIndex: null); // cold: database path
        var request = CreateIntentRequest(musician: "Beatles");

        await handler.HandleAsync(request, CreateContext(), user, CreateSession(), CancellationToken.None);

        _libraryManagerMock.Verify(
            l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null
                && q.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.MusicArtist)
                && q.IncludeItemsByName == true
                && q.TopParentIds != null
                && q.TopParentIds.Contains(libraryId))),
            Times.AtLeastOnce,
            "cold-window artist queries must set IncludeItemsByName under the TopParentIds filter, or folderless artists match zero rows");
    }

    [Fact]
    public async Task HandleAsync_ArtistSongsQuery_DoesNotUseMediaTypesAudio()
    {
        // JF-358 perf invariant: the artist-songs query must NOT set MediaTypes=Audio
        // (which returns the full library on Jellyfin 10.11.11). Only IncludeItemTypes filters.
        SetupUserMock();
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { new Audio { Name = "Yesterday", Id = Guid.NewGuid() } };
            });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "Beatles");

        await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        // The artist-songs query (2nd call) must NOT have MediaTypes=Audio set.
        _libraryManagerMock.Verify(
            l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ArtistIds != null && q.ArtistIds.Length > 0
                && (q.MediaTypes == null || q.MediaTypes.Length == 0))),
            Times.AtLeastOnce,
            "artist-songs query must NOT use MediaTypes=Audio (JF-358: it returns the full library on Jellyfin 10.11.11)");
    }

    [Fact]
    public async Task HandleAsync_NoArtistIndex_FallsBackToDatabase()
    {
        SetupUserMock();
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem>();
            });

        SetupSongResult(new Audio { Name = "Yesterday", Id = Guid.NewGuid() });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.AtLeastOnce);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_InMemoryIndex_FuzzyMatch()
    {
        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { artist };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);

        SetupUserMock();
        SetupSongResult(new Audio { Name = "Screenwriter's Blues", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "soul coughin"); // misspelling
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_MissingArtistName_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("artist", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_ArtistNotFound_ReturnsNotFound()
    {
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(new List<BaseItem>());

        SetupUserMock();

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Unknown");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Unknown", speech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_NoSongsForArtist_ReturnsNoSongs()
    {
        var artist = new MusicArtist { Name = "Empty Artist", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { artist };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Empty Artist");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// Verifies that the library filter is resolved only once per request in the database
    /// fallback path, even when all four search tiers are exercised.
    /// Before the optimization, ResolveTopParentIds was called once per query (up to 5 calls).
    /// After, it is called exactly once.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DatabaseFallback_ResolveLibraryFilterOnce()
    {
        // Arrange: user with library restriction so ResolveTopParentIds is invoked.
        var libraryId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { libraryId.ToString() });

        // The CollectionFolder resolves to a physical folder.
        var physicalFolderId = Guid.NewGuid();
        var cf = new CollectionFolder { Id = libraryId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };

        var physicalFolder = new Folder { Id = physicalFolderId };

        _libraryManagerMock.Setup(l => l.GetItemById(libraryId))
            .Returns(cf);
        _libraryManagerMock.Setup(l => l.FindByPath("/data/media/music", true))
            .Returns(physicalFolder);

        SetupUserMock();

        // No artist found in first 3 tiers, found in 4th tier -- forces all 4 tiers + artist songs query.
        int getItemListCallCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                getItemListCallCount++;
                if (getItemListCallCount == 4)
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Test Artist", Id = Guid.NewGuid() } };
                }

                return new List<BaseItem>();
            });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Test Song", Id = Guid.NewGuid() }
            });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "xyzzyfoo");
        var context = CreateContext();
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: response is valid (artist found and songs played)
        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);

        // GetItemById (used by ResolveTopParentIds) should be called exactly once,
        // not once per query tier. Before the optimization it would be called 5 times
        // (SearchTerm + PrefixFirstWord + PrefixFull + Contains + ArtistSongs).
        _libraryManagerMock.Verify(
            l => l.GetItemById(libraryId),
            Times.Once,
            "Library filter should be resolved exactly once per request, not once per query tier");
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// Verifies that all database fallback queries receive the same TopParentIds
    /// value (the pre-resolved filter), not re-resolved per tier.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DatabaseFallback_AllQueriesShareResolvedFilter()
    {
        // Arrange: user with 2 library restrictions
        var libraryId1 = Guid.NewGuid();
        var libraryId2 = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { libraryId1.ToString(), libraryId2.ToString() });

        var cf1 = new CollectionFolder { Id = libraryId1 };
        cf1.PhysicalLocationsList = new[] { "/media/music" };
        var cf2 = new CollectionFolder { Id = libraryId2 };
        cf2.PhysicalLocationsList = new[] { "/media/jazz" };

        var folder1 = new Folder { Id = Guid.NewGuid() };
        var folder2 = new Folder { Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemById(libraryId1)).Returns(cf1);
        _libraryManagerMock.Setup(l => l.GetItemById(libraryId2)).Returns(cf2);
        _libraryManagerMock.Setup(l => l.FindByPath("/media/music", true)).Returns(folder1);
        _libraryManagerMock.Setup(l => l.FindByPath("/media/jazz", true)).Returns(folder2);

        SetupUserMock();

        // Return an artist on the first tier
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { artist });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Comfortably Numb", Id = Guid.NewGuid() }
            });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "Pink Floyd");
        var context = CreateContext();
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert
        Assert.NotNull(response);

        // GetItemById should be called exactly twice (once per library ID), not 2*N per query
        _libraryManagerMock.Verify(l => l.GetItemById(libraryId1), Times.Once);
        _libraryManagerMock.Verify(l => l.GetItemById(libraryId2), Times.Once);
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// Verifies that the in-memory path also resolves the library filter only once.
    /// </summary>
    [Fact]
    public async Task HandleAsync_InMemoryPath_ResolveLibraryFilterOnce()
    {
        var libraryId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { libraryId.ToString() });

        var cf = new CollectionFolder { Id = libraryId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };
        var physicalFolder = new Folder { Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemById(libraryId))
            .Returns(cf);
        _libraryManagerMock.Setup(l => l.FindByPath("/data/media/music", true))
            .Returns(physicalFolder);

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>()))
            .Returns(new List<BaseItem> { new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() } });

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Yesterday", Id = Guid.NewGuid() }
            });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // In-memory path resolves the filter once for artist search and reuses it for songs query.
        // GetItemById is called exactly once (for the single pre-resolution).
        _libraryManagerMock.Verify(
            l => l.GetItemById(libraryId),
            Times.Once,
            "In-memory path should resolve library filter exactly once");
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_ShuffleArtistSongsOff_BuildsPopularityOrderQueue()
    {
        _config.ShuffleArtistSongs = false;

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>()))
            .Returns(new List<BaseItem> { artist });

        SetupUserMock();

        var songs = new[]
        {
            new Audio { Name = "Yesterday", Id = Guid.NewGuid() },
            new Audio { Name = "Let It Be", Id = Guid.NewGuid() },
            new Audio { Name = "Hey Jude", Id = Guid.NewGuid() }
        };
        SetupSongResult(songs);

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        // Queue should preserve original order when shuffle is off
        Assert.Equal(3, session.NowPlayingQueue.Count);
        Assert.Equal(songs[0].Id, session.NowPlayingQueue[0].Id);
        Assert.Equal(songs[1].Id, session.NowPlayingQueue[1].Id);
        Assert.Equal(songs[2].Id, session.NowPlayingQueue[2].Id);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_ShuffleArtistSongsOn_RandomizesQueueOrder()
    {
        _config.ShuffleArtistSongs = true;

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>()))
            .Returns(new List<BaseItem> { artist });

        SetupUserMock();

        // Use enough songs that a shuffle is statistically certain to differ from original order
        var songs = Enumerable.Range(0, 20)
            .Select(i => new Audio { Name = $"Song {i}", Id = Guid.NewGuid() })
            .ToArray();
        SetupSongResult(songs);

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(20, session.NowPlayingQueue.Count);

        // All items must be present (no duplicates, no losses)
        var queueIds = session.NowPlayingQueue.Select(q => q.Id).ToHashSet();
        Assert.Equal(songs.Length, queueIds.Count);
        Assert.All(songs, s => Assert.Contains(s.Id, queueIds));

        // With 20 items, the probability that shuffle produces the exact original order is 1/20! ≈ 4e-19
        bool anyReordered = false;
        for (int i = 0; i < songs.Length; i++)
        {
            if (session.NowPlayingQueue[i].Id != songs[i].Id)
            {
                anyReordered = true;
                break;
            }
        }

        Assert.True(anyReordered, "Shuffle should reorder at least one item in a 20-song queue");
        Assert.True(response.Response.ShouldEndSession);
    }
}