#nullable enable

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
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using User = Jellyfin.Plugin.AlexaSkill.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-325 voice playlist editing: add-current/add-song/remove-current/create.
/// Pins the "this" resolution order (AudioPlayer token before session item, the
/// CLAUDE.md resume rule), the tiered playlist-name match, the duplicate-create
/// refusal, and the no-playlists-folder failure message.
/// </summary>
[Collection("Plugin")]
public class PlaylistEditIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IUserManager> _userManagerMock = new();
    private readonly Mock<IPlaylistManager> _playlistManagerMock = new();
    private readonly PluginConfiguration _config = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    private static readonly Guid SongId = Guid.NewGuid();
    private static readonly Guid OtherSongId = Guid.NewGuid();
    private static readonly Guid PlaylistId = Guid.NewGuid();

    public PlaylistEditIntentHandlerTests()
    {
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        _libraryManagerMock.Setup(l => l.GetItemById(SongId)).Returns(new Audio { Name = "Circles", Id = SongId });
        _libraryManagerMock.Setup(l => l.GetItemById(OtherSongId)).Returns(new Audio { Name = "Other", Id = OtherSongId });
        _playlistManagerMock
            .Setup(p => p.GetPlaylists(It.IsAny<Guid>()))
            .Returns(new[] { new Playlist { Name = "Road Trip", Id = PlaylistId } });
    }

    private AddCurrentToPlaylistIntentHandler CreateAddCurrent() =>
        new(_playlistManagerMock.Object, _sessionManagerMock.Object, _config, _userManagerMock.Object, _libraryManagerMock.Object, _loggerFactory);

    private AddSongToPlaylistIntentHandler CreateAddSong() =>
        new(_playlistManagerMock.Object, _sessionManagerMock.Object, _config, _userManagerMock.Object, _libraryManagerMock.Object, _loggerFactory);

    private RemoveCurrentFromPlaylistIntentHandler CreateRemove() =>
        new(_playlistManagerMock.Object, _sessionManagerMock.Object, _config, _userManagerMock.Object, _libraryManagerMock.Object, _loggerFactory);

    private CreatePlaylistIntentHandler CreateCreate() =>
        new(_playlistManagerMock.Object, _sessionManagerMock.Object, _config, _userManagerMock.Object, _libraryManagerMock.Object, _loggerFactory);

    private static IntentRequest CreateRequest(string intentName, Dictionary<string, string>? slots = null, string dialogState = "COMPLETED")
    {
        var slotDict = new Dictionary<string, Slot>();
        foreach (var (name, value) in slots ?? new Dictionary<string, string>())
        {
            slotDict[name] = new Slot { Name = name, Value = value };
        }

        return new IntentRequest
        {
            Intent = new Intent { Name = intentName, Slots = slotDict },
            Locale = "en-US",
            Type = "IntentRequest",
            DialogState = dialogState
        };
    }

    private static Context CreateTokenContext(Guid itemId) => new()
    {
        AudioPlayer = new global::Alexa.NET.Request.Type.PlaybackState { Token = itemId.ToString() }
    };

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    [Fact]
    public async void AddCurrent_TokenResolves_AddsAndConfirms()
    {
        SkillResponse response = await CreateAddCurrent().HandleAsync(
            CreateRequest(IntentNames.AddCurrentToPlaylist, new() { ["playlist"] = "road trip" }),
            CreateTokenContext(SongId), CreateUser(), session: null!, CancellationToken.None);

        Assert.Contains("Circles", ResponseStrings.Get("AddedToPlaylist", "en-US", "Circles", "Road Trip"), StringComparison.Ordinal);
        VerifyAddItem(PlaylistId, id => id == SongId, Times.Once());
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async void AddCurrent_TokenFirst_SessionItemIgnoredWhenTokenResolves()
    {
        // Token names Circles; session names Other. The token must win (CLAUDE.md).
        SkillResponse response = await CreateAddCurrent().HandleAsync(
            CreateRequest(IntentNames.AddCurrentToPlaylist, new() { ["playlist"] = "Road Trip" }),
            CreateTokenContext(SongId), CreateUser(),
            CreateSession(OtherSongId), CancellationToken.None);

        VerifyAddItem(PlaylistId, id => id == SongId, Times.Once());
    }

    [Fact]
    public async void AddCurrent_NoToken_FallsBackToSessionItem()
    {
        SkillResponse response = await CreateAddCurrent().HandleAsync(
            CreateRequest(IntentNames.AddCurrentToPlaylist, new() { ["playlist"] = "road" }),
            new Context(), CreateUser(), CreateSession(OtherSongId), CancellationToken.None);

        // "road" prefix-matches "Road Trip"
        VerifyAddItem(PlaylistId, id => id == OtherSongId, Times.Once());
    }

    /// <summary>
    /// JF-626 fix (b): a held <c>FullNowPlayingItem</c> returns free; the
    /// pre-JF-626 shape re-resolved the session DTO's id through the library
    /// even though the full item was already in hand.
    /// </summary>
    [Fact]
    public async void AddCurrent_FullNowPlayingItemHeld_NoLibraryReResolve()
    {
        Guid sessionItemId = Guid.NewGuid();
        var sessionItem = new Audio { Name = "Held Item", Id = sessionItemId };
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.FullNowPlayingItem = sessionItem;

        await CreateAddCurrent().HandleAsync(
            CreateRequest(IntentNames.AddCurrentToPlaylist, new() { ["playlist"] = "road trip" }),
            new Context(), CreateUser(), session, CancellationToken.None);

        _libraryManagerMock.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
        VerifyAddItem(PlaylistId, id => id == sessionItemId, Times.Once());
    }

    [Fact]
    public async void AddCurrent_NothingPlaying_AnswersNoMediaPlaying()
    {
        SkillResponse response = await CreateAddCurrent().HandleAsync(
            CreateRequest(IntentNames.AddCurrentToPlaylist, new() { ["playlist"] = "road trip" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.Contains("playing", GetSpeech(response), StringComparison.OrdinalIgnoreCase);
        VerifyAddItemNever();
    }

    [Fact]
    public async void AddCurrent_UnknownPlaylist_AnswersNotFound()
    {
        SkillResponse response = await CreateAddCurrent().HandleAsync(
            CreateRequest(IntentNames.AddCurrentToPlaylist, new() { ["playlist"] = "nope" }),
            CreateTokenContext(SongId), CreateUser(), session: null!, CancellationToken.None);

        Assert.Contains("nope", GetSpeech(response), StringComparison.Ordinal);
        VerifyAddItemNever();
    }

    [Fact]
    public async void AddCurrent_EmptySlot_AnswersPlaylistPrompt()
    {
        SkillResponse response = await CreateAddCurrent().HandleAsync(
            CreateRequest(IntentNames.AddCurrentToPlaylist),
            CreateTokenContext(SongId), CreateUser(), session: null!, CancellationToken.None);

        // JF-600: the playlist-EDIT family speaks the neutral retry prompt; the
        // listen-worded DidNotCatchPlaylistName stays with PlayPlaylist.
        Assert.Equal(ResponseStrings.Get("SpecifyPlaylistName", "en-US"), GetSpeech(response));
    }

    [Fact]
    public async void AddSong_CarrierNounLeakedIntoSlotValue_StillFindsTheSong()
    {
        // Live profile-nlu 2026-09-19: "aggiungi la canzone {song} alla playlist X"
        // fills song with "canzone rapsodia" (the noun rides into the slot value).
        // The pick must accept a candidate the query ends with.
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<Audio> { new() { Name = "Rapsodia", Id = SongId } });

        SkillResponse response = await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "canzone rapsodia", ["playlist_target"] = "road trip" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        VerifyAddItem(PlaylistId, id => id == SongId, Times.Once());
    }

    [Fact]
    public async void RemoveCurrent_PassesItemGuidInNFormat()
    {
        SkillResponse response = await CreateRemove().HandleAsync(
            CreateRequest(IntentNames.RemoveCurrentFromPlaylist, new() { ["playlist"] = "road trip" }),
            CreateTokenContext(SongId), CreateUser(), session: null!, CancellationToken.None);

        _playlistManagerMock.Verify(
            p => p.RemoveItemFromPlaylistAsync(
                PlaylistId.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
                It.Is<IEnumerable<string>>(ids => ids.Contains(SongId.ToString("N", System.Globalization.CultureInfo.InvariantCulture)))),
            Times.Once());
    }

    [Fact]
    public async void CreatePlaylist_NewName_CreatesWithAudioType()
    {
        PlaylistCreationRequest? captured = null;
        _playlistManagerMock
            .Setup(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(r => captured = r)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString("N")));

        SkillResponse response = await CreateCreate().HandleAsync(
            CreateRequest(IntentNames.CreatePlaylist, new() { ["playlist"] = "Evening Jazz" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.Equal("Evening Jazz", captured!.Name);
        Assert.Equal(Jellyfin.Data.Enums.MediaType.Audio, captured.MediaType);
        Assert.Contains("Evening Jazz", GetSpeech(response), StringComparison.Ordinal);
    }

    [Fact]
    public async void CreatePlaylist_DuplicateName_RefusesInsteadOfName1()
    {
        SkillResponse response = await CreateCreate().HandleAsync(
            CreateRequest(IntentNames.CreatePlaylist, new() { ["playlist"] = "Road Trip" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.Equal(ResponseStrings.Get("PlaylistAlreadyExists", "en-US", "Road Trip"), GetSpeech(response));
        _playlistManagerMock.Verify(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()), Times.Never());
    }

    [Fact]
    public async void CreatePlaylist_SuperstringOfExistingName_Creates()
    {
        // JF-615: the duplicate check is EXACT-NAME only. A new name that merely
        // CONTAINS an existing one ("Road Trip 2" vs "Road Trip") must create;
        // the tiered FindPlaylist substring tier used to block it as a duplicate.
        PlaylistCreationRequest? captured = null;
        _playlistManagerMock
            .Setup(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(r => captured = r)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString("N")));

        await CreateCreate().HandleAsync(
            CreateRequest(IntentNames.CreatePlaylist, new() { ["playlist"] = "Road Trip 2" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Road Trip 2", captured!.Name);
    }

    [Fact]
    public async void CreatePlaylist_NoFolderFailure_AnswersCreateFailed()
    {
        _playlistManagerMock
            .Setup(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Throws(new ArgumentException(nameof(PlaylistCreationRequest)));

        SkillResponse response = await CreateCreate().HandleAsync(
            CreateRequest(IntentNames.CreatePlaylist, new() { ["playlist"] = "Evening Jazz" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.Equal(ResponseStrings.Get("PlaylistCreateFailed", "en-US"), GetSpeech(response));
    }

    [Fact]
    public async void AddSong_CalledCarrierLeakedIntoPlaylistTarget_StillFindsPlaylist()
    {
        // Live incident 2026-09-20 (JF-600): "crea una playlist chiamata prova echo"
        // misrouted to AddSong with playlist_target="chiamata prova echo". The strip
        // must let the tiered match see the spoken name. en-US carrier is "called".
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<Audio> { new() { Name = "Circles", Id = SongId } });

        await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "circles", ["playlist_target"] = "called road trip" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        VerifyAddItem(PlaylistId, id => id == SongId, Times.Once());
    }

    [Fact]
    public async void AddSong_SongMissing_ElicitsSongWithStrippedPlaylistName()
    {
        // JF-601: a question prompt must keep the mic open (elicit), not end the
        // session on a Tell; the speech still carries the stripped playlist name.
        SkillResponse response = await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { ["playlist_target"] = "called road trip" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        TestHelpers.AssertElicitsSlot(response, "song_query", IntentNames.AddSongToPlaylist, new[] { IntentNames.Slots.SongQuery, IntentNames.Slots.PlaylistTarget });
        // JF-614 redesign: the song turn asks for the song only (the playlist is
        // the SECOND dialog turn, asked after the song resolves).
        Assert.Equal(ResponseStrings.Get("SpecifySongForPlaylistNoPlaylist", "en-US"), GetSpeech(response));
        VerifyAddItemNever();
    }

    [Fact]
    public async void AddSong_GreedyCaptureWithPlaylistClause_SplitsAndAdds()
    {
        // JF-614 review: the song-only SearchQuery sample captures the whole
        // tail ("rapsodia to the playlist road trip"); the handler splits the
        // playlist clause out and completes the add in one turn.
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<Audio> { new() { Name = "Rapsodia", Id = SongId } });

        await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "rapsodia to the playlist road trip" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        VerifyAddItem(PlaylistId, id => id == SongId, Times.Once());
    }

    [Fact]
    public async void AddSong_SongResolved_PlaylistMissing_ElicitsPlaylistEchoingSong()
    {
        // JF-614 review: the playlist elicit runs AFTER the song resolves and
        // echoes the song value in the updatedIntent so the round-trip cannot
        // wipe it.
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<Audio> { new() { Name = "Circles", Id = SongId } });

        SkillResponse response = await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "circles" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        var elicit = response.Response.Directives?.FirstOrDefault(d => d.Type == "Dialog.ElicitSlot") as Jellyfin.Plugin.AlexaSkill.Alexa.Directive.ElicitSlotDirective;
        Assert.NotNull(elicit);
        // the directive is internal; assert via the serialized shape instead
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(elicit);
        Assert.Contains("\"circles\"", json, StringComparison.Ordinal);
        VerifyAddItemNever();
    }

    [Fact]
    public async void AddSong_PlaylistMissing_ElicitsPlaylistTarget()
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<Audio> { new() { Name = "Circles", Id = SongId } });

        SkillResponse response = await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "circles" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        TestHelpers.AssertElicitsSlot(response, "playlist_target", IntentNames.AddSongToPlaylist, new[] { IntentNames.Slots.SongQuery, IntentNames.Slots.PlaylistTarget });
        // Review round: pin the spoken prompt too, not just the directive shape.
        Assert.Equal(ResponseStrings.Get("SpecifyPlaylistName", "en-US"), GetSpeech(response));
        VerifyAddItemNever();
    }

    [Fact]
    public async void AddSong_CancelWordDuringOpenElicit_EndsFlow()
    {
        // JF-601 AC#4: an open elicit traps the next utterance into the slot; a
        // bare cancel word must end the flow instead of searching for "stop".
        SkillResponse response = await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "stop", ["playlist_target"] = "road trip" }, dialogState: "IN_PROGRESS"),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.Equal(ResponseStrings.Get("FlowCancelled", "en-US"), GetSpeech(response));
        Assert.True(response.Response.ShouldEndSession);
        VerifyAddItemNever();
    }

    [Fact]
    public async void AddSong_TrappedOneShotDuringOpenElicit_EndsFlowWithHint()
    {
        // JF-620 live incident: with the song question open, a full one-shot with
        // the invocation name is captured whole into the slot; it must end the flow
        // with the repeat hint instead of answering a nonsense not-found.
        SkillResponse response = await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "ask jellyfin player to turn on loop", ["playlist_target"] = "prova echo" }, dialogState: "IN_PROGRESS"),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.Equal(ResponseStrings.Get("ElicitTrapEscaped", "en-US"), GetSpeech(response));
        Assert.True(response.Response.ShouldEndSession);
        VerifyAddItemNever();
    }

    [Fact]
    public async void AddSong_PlaylistGenuinelyNamedWithCarrier_RawNameWins()
    {
        // JF-602: a playlist whose REAL name starts with the carrier word (created
        // via the Jellyfin web UI through the same server-wide playlist manager)
        // must keep its exact-tier match; the strip runs only on a miss.
        _playlistManagerMock
            .Setup(p => p.GetPlaylists(It.IsAny<Guid>()))
            .Returns(new[] { new Playlist { Name = "Called Road Trip", Id = PlaylistId } });
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<Audio> { new() { Name = "Circles", Id = SongId } });

        await CreateAddSong().HandleAsync(
            CreateRequest(IntentNames.AddSongToPlaylist, new() { [IntentNames.Slots.SongQuery] = "circles", ["playlist_target"] = "called road trip" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        VerifyAddItem(PlaylistId, id => id == SongId, Times.Once());
    }

    [Fact]
    public async void CreatePlaylist_CalledCarrierStripped_FromNewName()
    {
        PlaylistCreationRequest? captured = null;
        _playlistManagerMock
            .Setup(p => p.CreatePlaylist(It.IsAny<PlaylistCreationRequest>()))
            .Callback<PlaylistCreationRequest>(r => captured = r)
            .ReturnsAsync(new PlaylistCreationResult(Guid.NewGuid().ToString("N")));

        await CreateCreate().HandleAsync(
            CreateRequest(IntentNames.CreatePlaylist, new() { ["playlist"] = "called Evening Jazz" }),
            new Context(), CreateUser(), session: null!, CancellationToken.None);

        Assert.Equal("Evening Jazz", captured!.Name);
    }

    [Theory]
    [InlineData("it-IT", "chiamata prova echo", "prova echo")]
    [InlineData("it-IT", "chiamato Prova", "Prova")]
    [InlineData("en-US", "called Road Trip", "Road Trip")]
    [InlineData("fr-FR", "appelée nuit", "nuit")]
    [InlineData("de-DE", "namens Liste", "Liste")]
    [InlineData("ja-JP", "テスト という", "テスト")]
    [InlineData("ja-JP", "テストという", "テスト")]
    [InlineData("hi-IN", "prova नाम की", "prova")]
    [InlineData("hi-IN", "prova नाम का", "prova")]
    [InlineData("it-IT", "chiamata", "chiamata")]
    [InlineData("en-US", "la chiamata", "la chiamata")]
    [InlineData("it-IT", "  ", null)]
    [InlineData("it-IT", null, null)]
    [InlineData("xx-XX", "chiamata prova", "chiamata prova")]
    public void NormalizePlaylistName_StripsCarrierPerLocale(string locale, string? raw, string? expected)
    {
        Assert.Equal(expected, Jellyfin.Plugin.AlexaSkill.Alexa.Util.PlaylistNameNormalizer.NormalizePlaylistName(raw, locale));
    }

    private SessionInfo CreateSession(Guid itemId) => new(_sessionManagerMock.Object, _loggerFactory.CreateLogger<SessionInfo>())
    {
        NowPlayingItem = new BaseItemDto { Id = itemId }
    };

    private void VerifyAddItem(Guid playlistId, Func<Guid, bool> itemMatch, Times times)
    {
#if JELLYFIN_12
        _playlistManagerMock.Verify(p => p.AddItemToPlaylistAsync(playlistId, It.Is<IReadOnlyCollection<Guid>>(c => c.Any(itemMatch)), null, It.IsAny<Guid>()), times);
#else
        _playlistManagerMock.Verify(p => p.AddItemToPlaylistAsync(playlistId, It.Is<IReadOnlyCollection<Guid>>(c => c.Any(itemMatch)), It.IsAny<Guid>()), times);
#endif
    }

    private void VerifyAddItemNever()
    {
#if JELLYFIN_12
        _playlistManagerMock.Verify(p => p.AddItemToPlaylistAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<int?>(), It.IsAny<Guid>()), Times.Never());
#else
        _playlistManagerMock.Verify(p => p.AddItemToPlaylistAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<Guid>()), Times.Never());
#endif
    }

    private static string GetSpeech(SkillResponse response) =>
        ((PlainTextOutputSpeech)response.Response.OutputSpeech).Text;
}
