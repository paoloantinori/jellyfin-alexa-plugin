using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for FindSongIntentHandler state machine transitions.
/// </summary>
[Collection("Plugin")]
public class FindSongIntentHandlerTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly FindSongIntentHandler _handler;
    private readonly Guid _userId = Guid.NewGuid();

    public FindSongIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new FindSongIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);
        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, cfg => { }, "findsong-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    // ========== CanHandle ==========

    [Fact]
    public void CanHandle_FindSongIntent_ReturnsTrue()
    {
        var request = CreateIntentRequest("FindSongIntent");
        Assert.True(_handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_FindSongByArtistIntent_ReturnsTrue()
    {
        var request = CreateIntentRequest("FindSongByArtistIntent");
        Assert.True(_handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_FallbackIntent_ReturnsFalse()
    {
        // JF-397 review fix: FindSong must NOT claim FallbackIntent unconditionally.
        // The controller-level FindSongSessionData override routes fallbacks here while
        // a dialog is active; the CanHandle claim starved FallbackIntentHandler.
        var request = CreateIntentRequest("AMAZON.FallbackIntent");
        Assert.False(_handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var request = CreateIntentRequest("PlaySongIntent");
        Assert.False(_handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var request = new LaunchRequest();
        Assert.False(_handler.CanHandle(request));
    }

    // ========== First Invocation ==========

    [Fact]
    public async Task FirstInvocation_WithMusicianSlot_EntersAwaitingKeywords()
    {
        SetupJellyfinUser();
        SetupArtistSearch(Guid.NewGuid(), "Pink Floyd");
        var user = CreateTestUser();
        var session = CreateSession();

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["musician"] = "Pink Floyd"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, null, CancellationToken.None);

        // Should prompt for keywords (Ask = ShouldEndSession=false)
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.NotNull(response.SessionAttributes);

        // Verify session state
        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.AwaitingKeywords, sessionData.State);
        Assert.Equal("Pink Floyd", sessionData.ArtistName);
        Assert.NotNull(sessionData.ArtistId);
    }

    [Fact]
    public async Task CapturedCancelWord_WithOpenFlow_EndsFlowWithTell()
    {
        // Escape hatch from the elicitation trap, gated on an OPEN FindSong flow (an open
        // elicit always persists FindSongSessionData): while a Dialog.ElicitSlot is open,
        // Alexa captures the user's next utterance INTO the slot, so stop/cancel words
        // arrive here as keywords instead of AMAZON.Stop/CancelIntent (observed via
        // simulate-skill and the console 2026-08-28: "stop"/"ferma" fed the keywords and
        // the FindSong session never ended, hijacking every subsequent utterance).
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();
        var attrs = new Dictionary<string, object>
        {
            ["FindSongSessionData"] = JsonConvert.SerializeObject(new FindSongSessionData
            {
                State = FindSongState.AwaitingKeywords,
                ArtistName = "Test Artist"
            })
        };

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "stop"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, attrs, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession != false, "cancel word during open flow must end the session");
        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(speech), "cancel response must speak the FindSongCancelled string");
        Assert.True(response.Response.Directives?.Any(d => d.Type == "Dialog.ElicitSlot") != true, "cancel word must not re-elicit");
    }

    [Fact]
    public async Task CapturedCancelWord_FirstInvocation_StillSearches()
    {
        // Code-review 2026-08-29 regression lock: the hatch is gated on an open flow, so
        // a FIRST-invocation search for a song actually titled like a cancel word
        // ("trova la canzone stop") must still run the normal flow, not cancel.
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "stop"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, null, CancellationToken.None);

        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.DoesNotContain("interrotto", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopIntentForceRoutedWithOpenFlow_EndsFlowWithTell()
    {
        // Code-review 2026-08-29: the controller force-routes ANY IntentRequest with
        // FindSongSessionData here, including an NLU-resolved AMAZON.StopIntent which
        // arrives with NO titleKeywords slot; the hatch must treat the intent name as the
        // cancel signal or the hijack survives through this routing regime.
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();
        var attrs = new Dictionary<string, object>
        {
            ["FindSongSessionData"] = JsonConvert.SerializeObject(new FindSongSessionData
            {
                State = FindSongState.AwaitingKeywords,
                ArtistName = "Test Artist"
            })
        };

        var request = CreateIntentRequest("AMAZON.StopIntent");

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, attrs, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession != false, "force-routed StopIntent during open flow must end the session");
        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(speech), "cancel response must speak");
    }

    [Fact]
    public async Task FirstInvocation_WithTitleKeywordsSlot_EntersAwaitingArtist()
    {
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "comfortably numb"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, null, CancellationToken.None);

        // Should prompt for artist
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);

        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.AwaitingArtist, sessionData.State);
        Assert.Equal("comfortably numb", sessionData.Keywords);
    }

    [Fact]
    public async Task FirstInvocation_WithNeitherSlot_EntersAwaitingKeywords()
    {
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();

        var request = CreateIntentRequest("FindSongIntent");

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, null, CancellationToken.None);

        // Should prompt for keywords
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);

        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.AwaitingKeywords, sessionData.State);
    }

    [Fact]
    public async Task FirstInvocation_WithBothSlots_SkipsToAwaitingKeywords()
    {
        SetupJellyfinUser();
        SetupArtistSearch(Guid.NewGuid(), "Pink Floyd");
        var user = CreateTestUser();
        var session = CreateSession();

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["musician"] = "Pink Floyd",
            ["titleKeywords"] = "wish you were here"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, null, CancellationToken.None);

        // Musician slot is prioritized → AwaitingKeywords
        Assert.False(response.Response.ShouldEndSession);

        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.AwaitingKeywords, sessionData.State);
        Assert.Equal("Pink Floyd", sessionData.ArtistName);
        Assert.NotNull(sessionData.ArtistId);
    }

    // ========== AwaitingKeywords ==========

    [Fact]
    public async Task AwaitingKeywords_StopWordsOnly_ReturnsTooVague()
    {
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();

        // Set up existing session data in AwaitingKeywords state with artist
        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistName = "Pink Floyd"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        // Input is all stop words
        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "the a an of"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("more specific words", speech);
    }

    [Fact]
    public async Task AwaitingKeywords_ValidKeywords_WithArtist_SearchesSongs()
    {
        var artistId = Guid.NewGuid();
        var songId = Guid.NewGuid();

        SetupJellyfinUser();
        SetupArtistSearch(artistId, "Pink Floyd");
        SetupSongSearch(new List<BaseItem> { CreateAudioItem(songId, "Wish You Were Here") });

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "Pink Floyd"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "wish you were here"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Single match → auto-play with ShouldEndSession=true
        Assert.True(response.Response.ShouldEndSession);
    }

    // JF-383 generalization: the artist-scoped search pre-filters server-side with
    // NameContains=firstKeyword. When the spoken keyword is a full word and the tagged
    // title uses the abbreviation ("street" vs "Decatur St."), the substring pre-filter
    // returns ZERO candidates and KeywordMatcher never gets to canonicalize. The fix:
    // when the NameContains-filtered query comes back empty, retry with ArtistIds only
    // (one artist's songs, bounded) and let KeywordMatcher.Score decide.
    [Fact]
    public async Task AwaitingKeywords_ArtistScoped_NameContainsMiss_RetriesWithoutFilter_AndCanonicalizes()
    {
        var artistId = Guid.NewGuid();
        SetupJellyfinUser();

        // Live repro shape: NameContains pre-filter misses ("street" not a substring of
        // "Decatur St."), but the artist HAS the song.
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isAudioByArtist = q.ArtistIds != null && q.ArtistIds.Length > 0
                    && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio);
                if (isAudioByArtist && !string.IsNullOrEmpty(q.NameContains))
                {
                    return new List<BaseItem>().AsReadOnly(); // the starving pre-filter
                }

                if (isAudioByArtist)
                {
                    return new List<BaseItem> { CreateAudioItem(Guid.NewGuid(), "Decatur St.") }.AsReadOnly();
                }

                return new List<BaseItem>().AsReadOnly();
            });

        var user = CreateTestUser();
        var session = CreateSession();
        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "The Twilight Singers"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "street"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Single match found via the unfiltered retry + abbreviation canonicalization:
        // auto-plays (ShouldEndSession=true) instead of "Non ho trovato nulla".
        Assert.True(response.Response.ShouldEndSession);
    }

    // JF-384: accent drift on ONE keyword ("Decature" heard as "cater") must not veto the
    // whole artist-scoped match. The exact matcher (100% coverage) misses; the phonetic
    // stage (>=50% coverage + penalty, same semantics as the global n-gram path) finds
    // "Decatur St." via the un-drifted "street" token.
    [Fact]
    public async Task AwaitingKeywords_ArtistScoped_AccentDriftOnOneWord_PhoneticStageFinds()
    {
        var artistId = Guid.NewGuid();
        SetupJellyfinUser();

        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isAudioByArtist = q.ArtistIds != null && q.ArtistIds.Length > 0
                    && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio);
                if (isAudioByArtist)
                {
                    return new List<BaseItem> { CreateAudioItem(Guid.NewGuid(), "Decatur St.") }.AsReadOnly();
                }

                return new List<BaseItem>().AsReadOnly();
            });

        var user = CreateTestUser();
        var session = CreateSession();
        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "The Twilight Singers"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "cater street"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Found via the phonetic stage: auto-plays instead of "Non ho trovato nulla".
        Assert.True(response.Response.ShouldEndSession);
    }

    // JF-384 garbage control: a single keyword that matches nothing (0% coverage on both
    // matchers) must stay not-found even in the artist scope.
    [Fact]
    public async Task AwaitingKeywords_ArtistScoped_SingleGarbageKeyword_StillNotFound()
    {
        var artistId = Guid.NewGuid();
        SetupJellyfinUser();

        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isAudioByArtist = q.ArtistIds != null && q.ArtistIds.Length > 0
                    && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio);
                if (isAudioByArtist)
                {
                    return new List<BaseItem> { CreateAudioItem(Guid.NewGuid(), "Decatur St.") }.AsReadOnly();
                }

                return new List<BaseItem>().AsReadOnly();
            });

        var user = CreateTestUser();
        var session = CreateSession();
        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "The Twilight Singers"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "xyzzyfoo"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Not found: no AudioPlayer directive.
        Assert.True(response.Response?.Directives == null || response.Response.Directives.All(d => d.Type != "AudioPlayer.Play"));
    }

    // ========== AwaitingArtist ==========

    [Fact]
    public async Task AwaitingArtist_ArtistNotFound_ReturnsArtistNotFound()
    {
        SetupJellyfinUser();
        SetupArtistSearchEmpty();

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingArtist,
            Keywords = "comfortably numb"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["musician"] = "xyzzyfoo nonexistent"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("couldn't find an artist", speech);

        // State should remain AwaitingArtist
        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.AwaitingArtist, sessionData.State);
    }

    [Fact]
    public async Task AwaitingArtist_ArtistFound_ProceedsToSearch()
    {
        var artistId = Guid.NewGuid();
        var songId = Guid.NewGuid();

        SetupJellyfinUser();
        SetupArtistSearch(artistId, "Pink Floyd");
        SetupSongSearch(new List<BaseItem> { CreateAudioItem(songId, "Comfortably Numb") });

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingArtist,
            Keywords = "comfortably numb"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["musician"] = "Pink Floyd"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Should have found the song and auto-played
        Assert.True(response.Response.ShouldEndSession);
    }

    // ========== Search Results ==========

    [Fact]
    public async Task Search_NoMatches_ReturnsNoMatch()
    {
        var artistId = Guid.NewGuid();

        SetupJellyfinUser();
        SetupArtistSearch(artistId, "Pink Floyd");
        SetupSongSearch(new List<BaseItem>());

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "Pink Floyd"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "nonexistent song"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("couldn't find a match", speech);
    }

    [Fact]
    public async Task Search_NoArtistNoMatch_KeywordsMatchArtist_FallsBackToArtist()
    {
        // "Neither provided -> AwaitingKeywords" entry: no artist specified. Keywords that
        // find no song but match an artist should cross-media fall back to playing that
        // artist (mirrors PlaySong's "no musician slot" fallback), not re-prompt.
        var artistId = Guid.NewGuid();
        var artist = new MusicArtist();
        typeof(BaseItem).GetProperty("Id")!.SetValue(artist, artistId);
        typeof(BaseItem).GetProperty("Name")!.SetValue(artist, "Miles Davis");
        var song = CreateAudioItem(Guid.NewGuid(), "So What");

        SetupJellyfinUser();
        _libraryManagerMock.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isArtist = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist);
                bool hasArtistIds = q.ArtistIds != null && q.ArtistIds.Length > 0;
                bool isAudioMedia = q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio);
                if (isArtist)
                {
                    return new List<BaseItem> { artist }.AsReadOnly();
                }

                if (hasArtistIds && isAudioMedia)
                {
                    return new List<BaseItem> { song }.AsReadOnly();
                }

                return new List<BaseItem>().AsReadOnly(); // FindSong song search -> miss
            });

        var user = CreateTestUser();
        var session = CreateSession();
        var existingData = new FindSongSessionData { State = FindSongState.AwaitingKeywords };
        var sessionAttrs = BuildSessionAttributes(existingData);
        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "miles davis"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Cross-media fallback played the artist, not a re-prompt.
        Assert.True(response.Response?.Directives?.Any(d => d.Type == "AudioPlayer.Play") == true);
    }

    [Fact]
    public async Task Search_MultipleMatches_EntersDisambiguation()
    {
        var artistId = Guid.NewGuid();

        SetupJellyfinUser();
        SetupArtistSearch(artistId, "The Beatles");

        var songs = new List<BaseItem>
        {
            CreateAudioItem(Guid.NewGuid(), "Hey Jude"),
            CreateAudioItem(Guid.NewGuid(), "Hey There"),
            CreateAudioItem(Guid.NewGuid(), "Say Hey")
        };
        SetupSongSearch(songs);

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "The Beatles"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "hey"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Disambiguation → ShouldEndSession=false
        Assert.False(response.Response.ShouldEndSession);

        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.Disambiguating, sessionData.State);
        Assert.NotNull(sessionData.Candidates);
        Assert.True(sessionData.Candidates!.Count >= 1);
    }

    // ========== Disambiguating ==========

    [Fact]
    public async Task Disambiguating_ValidPickByNumber_ReturnsPlayback()
    {
        var songId = Guid.NewGuid();
        var song = CreateAudioItem(songId, "Hey Jude");
        SetupJellyfinUser();
        _libraryManagerMock.Setup(lm => lm.GetItemById(songId)).Returns(song);

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Candidates = new List<FindSongCandidate>
            {
                new(songId, "Hey Jude", "The Beatles", 95),
                new(Guid.NewGuid(), "Hey There", "The Beatles", 85)
            }
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "1"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Playback → ShouldEndSession=true
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task Disambiguating_ValidPickByOrdinal_ReturnsPlayback()
    {
        var songId = Guid.NewGuid();
        var song = CreateAudioItem(songId, "Hey There");
        SetupJellyfinUser();
        _libraryManagerMock.Setup(lm => lm.GetItemById(songId)).Returns(song);

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Candidates = new List<FindSongCandidate>
            {
                new(Guid.NewGuid(), "Hey Jude", "The Beatles", 95),
                new(songId, "Hey There", "The Beatles", 85)
            }
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "two"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Playback → ShouldEndSession=true
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task Disambiguating_ValidPickByPartialTitle_ReturnsPlayback()
    {
        var songId = Guid.NewGuid();
        var song = CreateAudioItem(songId, "Hey Jude");
        SetupJellyfinUser();
        _libraryManagerMock.Setup(lm => lm.GetItemById(songId)).Returns(song);

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Candidates = new List<FindSongCandidate>
            {
                new(songId, "Hey Jude", "The Beatles", 95),
                new(Guid.NewGuid(), "Let It Be", "The Beatles", 85)
            }
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "jude"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Playback → ShouldEndSession=true
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task Disambiguating_InvalidPick_ReturnsInvalidPick()
    {
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Candidates = new List<FindSongCandidate>
            {
                new(Guid.NewGuid(), "Hey Jude", "The Beatles", 95),
                new(Guid.NewGuid(), "Let It Be", "The Beatles", 85)
            }
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "something completely different"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Invalid pick → ShouldEndSession=false, keep Disambiguating state
        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("didn't catch that", speech);

        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.Disambiguating, sessionData.State);
    }

    [Fact]
    public async Task Disambiguating_NoSlotValue_ReturnsInvalidPick()
    {
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Candidates = new List<FindSongCandidate>
            {
                new(Guid.NewGuid(), "Hey Jude", "The Beatles", 95)
            }
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent");

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("didn't catch that", speech);
    }

    // ========== Session Data Serialization ==========

    [Fact]
    public void ReadSessionData_NullAttributes_ReturnsNull()
    {
        var result = FindSongIntentHandler.ReadSessionData(null);
        Assert.Null(result);
    }

    [Fact]
    public void ReadSessionData_NoFindSongKey_ReturnsNull()
    {
        var attrs = new Dictionary<string, object> { ["other"] = "value" };
        var result = FindSongIntentHandler.ReadSessionData(attrs);
        Assert.Null(result);
    }

    [Fact]
    public void ReadSessionData_ValidJson_ReturnsSessionData()
    {
        var data = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistName = "Test Artist"
        };

        var attrs = new Dictionary<string, object>
        {
            ["FindSongSessionData"] = JsonConvert.SerializeObject(data)
        };

        var result = FindSongIntentHandler.ReadSessionData(attrs);

        Assert.NotNull(result);
        Assert.Equal(FindSongState.AwaitingKeywords, result.State);
        Assert.Equal("Test Artist", result.ArtistName);
    }

    [Fact]
    public void ReadSessionData_InvalidJson_ReturnsNull()
    {
        var attrs = new Dictionary<string, object>
        {
            ["FindSongSessionData"] = "not valid json{{"
        };

        var result = FindSongIntentHandler.ReadSessionData(attrs);
        Assert.Null(result);
    }

    // ========== ResolvePick (static helper) ==========

    [Fact]
    public void ResolvePick_ByNumber_ReturnsCorrectIndex()
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick("1", candidates, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_ByNumberTwo_ReturnsIndex1()
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick("2", candidates, "en-US");
        Assert.Equal(1, result);
    }

    [Fact]
    public void ResolvePick_ByOrdinalOne_ReturnsIndex0()
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick("one", candidates, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_ByOrdinalTwo_ReturnsIndex1()
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick("two", candidates, "en-US");
        Assert.Equal(1, result);
    }

    // ========== JF-396: cardinal + ordinal pick words for es/pt/fr/nl ==========

    [Theory]
    [InlineData("dos", 1)]          // es cardinal
    [InlineData("tres", 2)]
    [InlineData("cuatro", 3)]
    [InlineData("segundo", 1)]      // es ordinal
    [InlineData("la tercera", 2)]
    [InlineData("el cuarto", 3)]
    [InlineData("dois", 1)]         // pt cardinal
    [InlineData("três", 2)]
    [InlineData("o terceiro", 2)]   // pt ordinal
    [InlineData("a quarta", 3)]
    [InlineData("deuxième", 1)]     // fr ordinal
    [InlineData("le premier", 0)]
    [InlineData("le troisième", 2)]
    [InlineData("le quatrième", 3)]
    [InlineData("tweede", 1)]       // nl ordinal
    [InlineData("derde", 2)]
    [InlineData("vierde", 3)]
    [InlineData("eerste", 0)]       // nl eerste contains erste, was already matched
    public void ResolvePick_EsPtFrNl_Answers_ResolveCorrectIndex(string input, int expected)
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick(input, candidates, "es-ES");
        Assert.Equal(expected, result);
    }

    // Guards: existing en/it behavior unchanged after the refactor
    [Theory]
    [InlineData("the second one", 1)]
    [InlineData("il terzo", 2)]
    [InlineData("zweite", 1)]
    [InlineData("un", 0)]
    [InlineData("quattro", 3)]
    [InlineData("no", null)]        // negative answers are NOT picks (JF-395 exit path)
    [InlineData("banana", null)]    // non-matching word is not a pick
    public void ResolvePick_ExistingWords_Unchanged(string input, int? expected)
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick(input, candidates, "en-US");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePick_ByPartialTitle_ReturnsMatchingIndex()
    {
        var candidates = new List<FindSongCandidate>
        {
            new(Guid.NewGuid(), "Hey Jude", null, 95),
            new(Guid.NewGuid(), "Let It Be", null, 85)
        };

        var result = FindSongIntentHandler.ResolvePick("jude", candidates, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_NoMatch_ReturnsNull()
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick("something unrelated", candidates, "en-US");
        Assert.Null(result);
    }

    [Fact]
    public void ResolvePick_TheFirstOne_ReturnsIndex0()
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick("the first one", candidates, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_IlPrimo_ReturnsIndex0()
    {
        var candidates = CreateTestCandidates(4);
        var result = FindSongIntentHandler.ResolvePick("il primo", candidates, "it-IT");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_EmptyInput_ReturnsNull()
    {
        var candidates = CreateTestCandidates(2);
        Assert.Null(FindSongIntentHandler.ResolvePick("", candidates, "en-US"));
        Assert.Null(FindSongIntentHandler.ResolvePick("   ", candidates, "en-US"));
    }

    [Fact]
    public void ResolvePick_EmptyCandidates_ReturnsNull()
    {
        var candidates = new List<FindSongCandidate>();
        Assert.Null(FindSongIntentHandler.ResolvePick("1", candidates, "en-US"));
    }

    // ========== FallbackIntent without session ==========

    // ========== Arbitrary Intent Routing (session-based override) ==========

    [Fact]
    public async Task AwaitingKeywords_ShowMoreIntent_ExtractsFromAnySlot()
    {
        // Simulates: user is in FindSong dialog, NLU routes "family" to ShowMoreIntent
        // ShowMoreIntent has no standard slots, so we test with a slot that carries
        // the text under an arbitrary name (mimicking what NLU might produce).
        var artistId = Guid.NewGuid();
        var songId = Guid.NewGuid();

        SetupJellyfinUser();
        SetupArtistSearch(artistId, "Beirut");
        SetupSongSearch(new List<BaseItem> { CreateAudioItem(songId, "Family") });

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "Beirut"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        // ShowMoreIntent with an arbitrary slot containing the user's text
        var request = CreateIntentRequest("ShowMoreIntent", new Dictionary<string, string?>
        {
            ["showMoreSlot"] = "family"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Should have found the song and auto-played (single match)
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task AwaitingKeywords_ShowMoreIntent_NoSlots_PromptsForKeywords()
    {
        // ShowMoreIntent with no slots at all — can't extract keywords
        SetupJellyfinUser();
        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistName = "Beirut"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        // ShowMoreIntent with no slots
        var request = CreateIntentRequest("ShowMoreIntent");

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Can't extract keywords → should re-prompt
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);

        // State should remain AwaitingKeywords
        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.AwaitingKeywords, sessionData.State);
    }

    [Fact]
    public async Task AwaitingKeywords_BrowseLibraryIntent_ExtractsFromBrowseCategorySlot()
    {
        // Simulates: user is in FindSong dialog, NLU routes "family" to BrowseLibraryIntent
        // which has a "browse_category" slot that captures "family"
        var artistId = Guid.NewGuid();
        var songId = Guid.NewGuid();

        SetupJellyfinUser();
        SetupArtistSearch(artistId, "Beirut");
        SetupSongSearch(new List<BaseItem> { CreateAudioItem(songId, "Family") });

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            ArtistId = artistId,
            ArtistName = "Beirut"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        // BrowseLibraryIntent with browse_category slot
        var request = CreateIntentRequest("BrowseLibraryIntent", new Dictionary<string, string?>
        {
            ["browse_category"] = "family"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Should have found the song and auto-played
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task AwaitingArtist_ShowMoreIntent_ExtractsFromAnySlot()
    {
        // Simulates: user is in AwaitingArtist state, NLU routes "beirut" to ShowMoreIntent
        var artistId = Guid.NewGuid();
        var songId = Guid.NewGuid();

        SetupJellyfinUser();
        SetupArtistSearch(artistId, "Beirut");
        SetupSongSearch(new List<BaseItem> { CreateAudioItem(songId, "Family") });

        var user = CreateTestUser();
        var session = CreateSession();

        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingArtist,
            Keywords = "family"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("ShowMoreIntent", new Dictionary<string, string?>
        {
            ["someSlot"] = "Beirut"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Should have found the artist and song, then auto-played
        Assert.True(response.Response.ShouldEndSession);
    }

    // ========== GetAnySlotValue ==========

    [Fact]
    public void GetAnySlotValue_NoSlots_ReturnsNull()
    {
        var request = CreateIntentRequest("ShowMoreIntent");
        Assert.Null(FindSongIntentHandler.GetAnySlotValue(request));
    }

    [Fact]
    public void GetAnySlotValue_WithSlotValue_ReturnsValue()
    {
        var request = CreateIntentRequest("SomeIntent", new Dictionary<string, string?>
        {
            ["arbitrarySlot"] = "family"
        });
        Assert.Equal("family", FindSongIntentHandler.GetAnySlotValue(request));
    }

    [Fact]
    public void GetAnySlotValue_EmptySlotValue_ReturnsNull()
    {
        var request = CreateIntentRequest("SomeIntent", new Dictionary<string, string?>
        {
            ["emptySlot"] = ""
        });
        Assert.Null(FindSongIntentHandler.GetAnySlotValue(request));
    }

    // ========== Too Many Results Without Artist ==========

    [Fact]
    public async Task Search_TooManyWithoutArtist_ReturnsTooManyNarrow()
    {
        SetupJellyfinUser();

        // Return 5 songs to trigger the "too many" path
        var songs = Enumerable.Range(0, 5)
            .Select(i => (BaseItem)CreateAudioItem(Guid.NewGuid(), $"Song with love part {i}"))
            .ToList();
        SetupSongSearch(songs);

        var user = CreateTestUser();
        var session = CreateSession();

        // No artist, only keywords
        var existingData = new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            Keywords = "love"
        };
        var sessionAttrs = BuildSessionAttributes(existingData);

        var request = CreateIntentRequest("FindSongIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "love"
        });

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, sessionAttrs, CancellationToken.None);

        // Too many results → ask for artist
        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("many songs with those words", speech);

        var sessionData = ReadSessionData(response);
        Assert.NotNull(sessionData);
        Assert.Equal(FindSongState.AwaitingArtist, sessionData.State);
    }

    // ========== Helper Methods ==========

    private static IntentRequest CreateIntentRequest(string intentName, Dictionary<string, string?>? slots = null)
    {
        var intent = new Intent { Name = intentName };
        if (slots != null)
        {
            intent.Slots = new Dictionary<string, Slot>();
            foreach (var kvp in slots)
            {
                intent.Slots[kvp.Key] = new Slot { Name = kvp.Key, Value = kvp.Value };
            }
        }

        return new IntentRequest
        {
            Intent = intent,
            Locale = "en-US"
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private Entities.User CreateTestUser() => TestHelpers.CreateTestUser(_userId);

    private void SetupJellyfinUser()
    {
        var jellyfinUser = new JellyfinUser("testuser", "test", "test") { Id = _userId };
        _userManagerMock.Setup(um => um.GetUserById(_userId)).Returns(jellyfinUser);
        // Also handle any Guid for ResolveJellyfinUser
        _userManagerMock.Setup(um => um.GetUserById(It.IsAny<Guid>())).Returns(jellyfinUser);
    }

    private void SetupArtistSearch(Guid artistId, string artistName)
    {
        var artist = new MusicArtist();
        typeof(BaseItem).GetProperty("Id")!.SetValue(artist, artistId);
        typeof(BaseItem).GetProperty("Name")!.SetValue(artist, artistName);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { artist }.AsReadOnly());
    }

    private void SetupArtistSearchEmpty()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>().AsReadOnly());
    }

    private void SetupSongSearch(List<BaseItem> songs)
    {
        // First call returns artists, subsequent calls return songs.
        // We use a call counter to differentiate.
        int callCount = 0;
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                // If we already have songs setup, return them for non-first calls
                return songs.AsReadOnly();
            });

        // Also setup GetItemById for each song
        foreach (var song in songs)
        {
            var id = song.Id;
            _libraryManagerMock.Setup(lm => lm.GetItemById(id)).Returns(song);
        }
    }

    private static Audio.Audio CreateAudioItem(Guid id, string name)
    {
        var audio = new Audio.Audio();
        typeof(BaseItem).GetProperty("Id")!.SetValue(audio, id);
        typeof(BaseItem).GetProperty("Name")!.SetValue(audio, name);
        return audio;
    }

    private static Dictionary<string, object> BuildSessionAttributes(FindSongSessionData data)
    {
        return new Dictionary<string, object>
        {
            ["FindSongSessionData"] = JsonConvert.SerializeObject(data)
        };
    }

    private static FindSongSessionData? ReadSessionData(SkillResponse response)
    {
        if (response.SessionAttributes == null
            || !response.SessionAttributes.ContainsKey("FindSongSessionData"))
        {
            return null;
        }

        string json = response.SessionAttributes["FindSongSessionData"]?.ToString() ?? "";
        return JsonConvert.DeserializeObject<FindSongSessionData>(json);
    }

    private static List<FindSongCandidate> CreateTestCandidates(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new FindSongCandidate(Guid.NewGuid(), $"Song {i + 1}", null, 90 - i * 5))
            .ToList();
    }

    // ========== JF-395: exit-by-no in Disambiguating ==========

    private static Dictionary<string, object> DisambiguatingAttrs(List<FindSongCandidate> candidates)
    {
        var data = new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Keywords = "song",
            Candidates = candidates
        };
        return new Dictionary<string, object>
        {
            ["FindSongSessionData"] = JsonConvert.SerializeObject(data)
        };
    }

    private async Task<SkillResponse> DisambiguateWithInput(string input, string locale = "en-US")
    {
        var user = CreateTestUser();
        var session = CreateSession();
        var attrs = DisambiguatingAttrs(CreateTestCandidates(3));
        var request = CreateIntentRequest("AMAZON.FallbackIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = input
        });
        request.Locale = locale;
        return await _handler.HandleAsync(request, CreateContext(), user, session, attrs, CancellationToken.None);
    }

    [Theory]
    [InlineData("no", "en-US")]
    [InlineData("none of them", "en-US")]
    [InlineData("nope", "en-US")]
    [InlineData("nessuna", "it-IT")]
    [InlineData("nessuno di questi", "it-IT")]
    [InlineData("keine", "de-DE")]
    [InlineData("aucune", "fr-FR")]
    [InlineData("ninguna", "es-ES")]
    [InlineData("nenhuma", "pt-BR")]
    public async Task Disambiguating_NegativeAnswer_EndsSessionCleanly(string input, string locale)
    {
        SkillResponse response = await DisambiguateWithInput(input, locale);

        // Clean exit: a Tell that ends the session, NOT the FindSongInvalidPick re-prompt loop
        Assert.True(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        string invalidPick = Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("FindSongInvalidPick", locale);
        Assert.NotEqual(invalidPick, speech);
    }

    // Review regression: a bare "no" must EXIT even when a candidate title merely
    // contains the substring ("No Surprises") - the title matcher ran first before.
    [Fact]
    public async Task Disambiguating_SingleTokenNo_ExitsEvenWhenTitleContainsIt()
    {
        var user = CreateTestUser();
        var session = CreateSession();
        var candidates = new List<FindSongCandidate>
        {
            new(Guid.NewGuid(), "No Surprises", null, 95),
            new(Guid.NewGuid(), "Creep", null, 85)
        };
        var attrs = DisambiguatingAttrs(candidates);
        var request = CreateIntentRequest("AMAZON.FallbackIntent", new Dictionary<string, string?>
        {
            ["titleKeywords"] = "no"
        });
        request.Locale = "en-US";

        SkillResponse response = await _handler.HandleAsync(request, CreateContext(), user, session, attrs, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        string abandoned = Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("FindSongDisambigAbandoned", "en-US");
        Assert.Equal(abandoned, TestHelpers.GetSpeechText(response));
    }

    // Review regression: a multi-token answer matching a candidate TITLE must win over
    // the ordinal stem it contains ("Second Chance" is a title, not rank 2).
    [Fact]
    public void ResolvePick_TitleWithOrdinalWord_TitleWinsOverRank()
    {
        var candidates = new List<FindSongCandidate>
        {
            new(Guid.NewGuid(), "First Cut Is the Deepest", null, 90),
            new(Guid.NewGuid(), "Second Chance", null, 85)
        };

        // "second chance" must pick the TITLE at index 1 because it is the second
        // candidate, NOT because "second" maps to rank 1: verify with the title at a
        // different position too.
        var result = FindSongIntentHandler.ResolvePick("second chance", candidates, "en-US");
        Assert.Equal(1, result);

        var reordered = new List<FindSongCandidate> { candidates[1], candidates[0] };
        var result2 = FindSongIntentHandler.ResolvePick("second chance", reordered, "en-US");
        Assert.Equal(0, result2); // the title, wherever it sits
    }

    [Fact]
    public async Task Disambiguating_UnparsableNonNegative_StillRePrompts()
    {
        // Regression guard: a non-negative unrecognizable answer keeps the existing behavior
        SkillResponse response = await DisambiguateWithInput("banana");

        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        string invalidPick = Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("FindSongInvalidPick", "en-US");
        Assert.Equal(invalidPick, speech);
    }
}
