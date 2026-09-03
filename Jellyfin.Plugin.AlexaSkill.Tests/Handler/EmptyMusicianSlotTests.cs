using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for truncated utterances where the musician slot arrives as empty string.
/// Example: "chiedi a jellyfin player di mettere the idiot kings dei"
/// matches PlaySongIntent "Di mettere {song} dei {musician}" with musician="".
/// </summary>
[Collection("Plugin")]
public class EmptyMusicianSlotTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public EmptyMusicianSlotTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlaySongIntentHandler CreateSongHandler()
    {
        return new PlaySongIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private PlayAlbumIntentHandler CreateAlbumHandler()
    {
        return new PlayAlbumIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateSongIntentRequest(string song, string? musician)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };

        if (musician != null)
        {
            intent.Slots["musician"] = new Slot { Name = "musician", Value = musician };
        }

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    private static IntentRequest CreateAlbumIntentRequest(string album, string? musician)
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["album"] = new Slot { Name = "album", Value = album }
        };

        if (musician != null)
        {
            intent.Slots["musician"] = new Slot { Name = "musician", Value = musician };
        }

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    [Fact]
    public async Task PlaySong_CapturedCancelWordDuringOpenElicit_EndsFlow()
    {
        // JF-413/code-review 2026-08-29: while the song-name Dialog.ElicitSlot is open, a
        // stop/cancel word is captured into the song slot (dialogState IN_PROGRESS)
        // instead of routing to AMAZON.Stop/Cancel; the handler must end the flow.
        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest("ferma", null);
        request.DialogState = "IN_PROGRESS";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession != false, "captured cancel word must end the session");
    }

    [Fact]
    public async Task PlaySong_FirstInvocationCancelWordTitle_StillSearches()
    {
        // Gated on IN_PROGRESS: a first invocation for a song actually titled like a
        // cancel word must run the normal search, not cancel.
        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest("stop", null);
        request.DialogState = "STARTED";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.DoesNotContain("interrotto", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayAlbum_CapturedCancelWordDuringOpenElicit_EndsFlow()
    {
        // JF-413/code-review 2026-08-29: same trap on the album/musician elicit.
        var handler = CreateAlbumHandler();
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["album"] = new Slot { Name = "album", Value = "ferma" }
        };
        var request = new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req", DialogState = "IN_PROGRESS" };
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession != false, "captured cancel word must end the session");
    }

    [Fact]
    public async Task PlaySong_FirstInvocationCancelWordMusician_StillSearches()
    {
        // JF-445: PlaySong's hatch stays IN_PROGRESS-only, DELIBERATELY not widened to
        // the STARTED force-route shape (that widening lives in FindSong alone): this
        // elicit persists no session state and no force-route delivers sibling requests
        // here, so a bare cancel word in a STARTED request is indistinguishable from a
        // fresh search for a real artist named exactly like the word (Basta). It must
        // search (here: continue the song elicit), not cancel.
        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest(string.Empty, "basta");
        request.DialogState = "STARTED";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.DoesNotContain("interrotto", speech, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Response.ShouldEndSession, "STARTED bare word with no open flow must elicit the song, not cancel");
    }

    [Fact]
    public async Task PlayAlbum_FirstInvocationCancelWordMusician_StillSearches()
    {
        // JF-445 mirror of the PlaySong lock: the STARTED widening does not apply to
        // PlayAlbum either; a bare "basta" in musician resolves the artist and plays an
        // album (the JF-411 path), never cancels.
        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntentRequest(string.Empty, "basta");
        request.DialogState = "STARTED";
        SetupUserMock();

        var artist = new MusicArtist { Name = "Basta", Id = Guid.NewGuid() };
        var album = new MusicAlbum { Name = "Basta Debut", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Title Track", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true)
                {
                    return new List<BaseItem> { artist };
                }

                return new List<BaseItem> { album };
            });
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.DoesNotContain("interrotto", speech, StringComparison.OrdinalIgnoreCase);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective);
        Assert.NotNull(playDirective);
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public async Task PlaySong_EmptySongSlot_ElicitsSongViaDialogDirective()
    {
        // JF-413 audit: ElicitSongName was a plain Ask with no flow state - the user's
        // answer (a bare song title) had to re-match through general NLU, and any
        // already-filled musician slot was discarded: the same context-loss shape as the
        // album elicit fixed on 2026-08-28. Dialog.ElicitSlot keeps the session in the
        // PlaySongIntent dialog (registered in dialog.intents in all 17 locales), the
        // answer fills the song slot, and the updatedIntent must declare BOTH intent
        // slots (Amazon rejects partial updatedIntent, live INVALID_RESPONSE 2026-08-28).
        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest(string.Empty, null);
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot") as Jellyfin.Plugin.AlexaSkill.Alexa.Directive.ElicitSlotDirective;
        Assert.NotNull(elicit);
        Assert.Equal("song", elicit.SlotToElicit);
        Assert.Equal("PlaySongIntent", elicit.UpdatedIntent.Name);
        Assert.Equal(new[] { "musician", "song" }, elicit.UpdatedIntent.Slots.Keys.OrderBy(k => k).ToArray());
        // JF-398: the elicit owns no flow state, so every OTHER flow's keys must be
        // marked for removal (no stale resume/disambiguation/pagination rides along).
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey(Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.SessionAttributeRemoval.MarkerKey), "elicit must mark other flows inactive");
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    // ── PlaySongIntentHandler ──────────────────────────────────────────────

    [Fact]
    public async Task PlaySong_EmptyMusicianSlot_SearchesSongWithoutArtistFilter()
    {
        // Simulates: "di mettere the idiot kings dei" → song="the idiot kings", musician=""
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };

        SetupUserMock();

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                // First call = song search, should return the song
                return new List<BaseItem> { song };
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest("the idiot kings", musician: "");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should play the song, NOT fail with "artist not found"
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);

        // Should have searched only once (song search), NOT twice (artist search + song search)
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task PlaySong_WhitespaceMusicianSlot_SearchesSongWithoutArtistFilter()
    {
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest("the idiot kings", musician: "   ");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task PlaySong_NullMusicianSlot_SearchesSongWithoutArtistFilter()
    {
        // Slot not present at all — baseline case, should already work
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest("the idiot kings", musician: null);
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task PlaySong_ValidMusicianSlot_AppliesArtistFilter()
    {
        // When musician is a real value, the artist filter should be applied
        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var song = new Audio { Name = "The Idiot Kings", Id = Guid.NewGuid() };

        SetupUserMock();

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem> { artist }, // artist search
                    _ => new List<BaseItem> { song }    // song search
                };
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest("the idiot kings", musician: "soul coughing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        // Two calls: artist search + song search with artist filter
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task PlaySong_EmptyMusicianSlot_NotFound_ReportsSongNotFound()
    {
        // Empty musician slot + song not found → "song not found" (not "artist not found")
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateSongHandler();
        var request = CreateSongIntentRequest("nonexistent song", musician: "");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Should end the session (Tell, not Ask) — song not found
        Assert.True(response.Response?.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("artista", speech);
    }

    // ── PlayAlbumIntentHandler ─────────────────────────────────────────────

    [Fact]
    public async Task PlayAlbum_EmptyMusicianSlot_SearchesAlbumWithoutArtistFilter()
    {
        var album = new MusicAlbum { Name = "Ruby Vroom", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Is Chicago", Id = Guid.NewGuid() };

        SetupUserMock();

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return new List<BaseItem> { album };
            });

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(new List<BaseItem> { track }));

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntentRequest("ruby vroom", musician: "");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task PlayAlbum_ValidMusicianSlot_AppliesArtistFilter()
    {
        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var album = new MusicAlbum { Name = "Ruby Vroom", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Is Chicago", Id = Guid.NewGuid() };

        SetupUserMock();

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem> { artist },
                    _ => new List<BaseItem> { album }
                };
            });

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(new List<BaseItem> { track }));

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntentRequest("ruby vroom", musician: "soul coughing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task PlayAlbum_EmptyMusicianSlot_NotFound_ReportsAlbumNotFound()
    {
        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateAlbumHandler();
        var request = CreateAlbumIntentRequest("nonexistent album", musician: "");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.True(response.Response?.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("artista", speech);
    }
}
