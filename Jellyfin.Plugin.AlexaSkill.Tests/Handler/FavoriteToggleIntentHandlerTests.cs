using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for the favorite-toggle family on the ONE shared current-item resolver
/// (JF-629, mirroring RateItemIntentHandlerTests' fixture shapes): the AudioPlayer
/// token survives PlaybackStopped clearing the session's now-playing DTO (the
/// documented resume gotcha: the DTO is gone, the token is not), and a stale audio
/// token loses to a VideoApp-routed ledger entry (the displacement shape the
/// RateItem/Repeat siblings already honor). The write itself lands on Jellyfin
/// UserItemData.IsFavorite.
/// </summary>
[Collection("Plugin")]
public class FavoriteToggleIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private MarkFavoriteIntentHandler CreateHandler(DeviceQueueManager? queueManager = null)
    {
        return new MarkFavoriteIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.UserDataManager.Object,
            _fx.UserManager.Object,
            _fx.LibraryManager.Object,
            _fx.LoggerFactory,
            queueManager);
    }

    private static IntentRequest Request(string intentName = IntentNames.MarkFavorite)
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Locale = "en-US",
            Intent = new Intent
            {
                Name = intentName,
                Slots = new Dictionary<string, Slot>()
            }
        };
    }

    /// <summary>
    /// The shared happy-path setup: the item resolves in the library, the Jellyfin
    /// user resolves, and GetUserData hands back a capturable UserItemData
    /// instance (the handler mutates it in place before saving).
    /// </summary>
    private UserItemData SetupHappyPath(BaseItem item)
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemById(item.Id)).Returns(item);
        var data = new UserItemData { Key = item.Id.ToString(), IsFavorite = false };
        _fx.UserDataManager.Setup(u => u.GetUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(data);
        return data;
    }

    [Fact]
    public void CanHandle_MarkFavoriteIntent_ReturnsTrue()
    {
        var handler = CreateHandler();

        Assert.True(handler.CanHandle(Request()));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();

        Assert.False(handler.CanHandle(Request(IntentNames.RateItem)));
    }

    /// <summary>
    /// The token-survives-PlaybackStopped shape (JF-629): Jellyfin clears the
    /// session's now-playing DTO when playback stops, but the device's
    /// AudioPlayer token survives. The pre-migration handler read ONLY the DTO and
    /// answered not-found here; the shared resolver resolves the track from the
    /// token and the toggle lands on it.
    /// </summary>
    [Fact]
    public async Task HandleAsync_StoppedClearedDto_TokenResolves_TogglesTokenTrack()
    {
        var song = new Audio { Name = "Super Bon Bon", Id = Guid.NewGuid(), Path = "/music/s.mp3" };
        var data = SetupHappyPath(song);
        var handler = CreateHandler();
        var session = _fx.CreateSession();
        Assert.Null(session.NowPlayingItem);

        var response = await handler.HandleAsync(
            Request(), TestHelpers.CreateContextWithToken(song.Id.ToString(), "fav-stopped-device"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(data.IsFavorite, "the token-resolved track is marked favorite");
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.Is<BaseItem>(i => i.Id == song.Id),
            data,
            UserDataSaveReason.UpdateUserRating,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("added to favorites", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.True(response.Response.ShouldEndSession, "the confirmation is a Tell");
    }

    /// <summary>
    /// The VideoApp displacement shape (JF-629, the Repeat/RateItem precedent):
    /// the stale music token names the last SONG while a VideoApp-routed movie
    /// plays. The video is current, so the toggle must land on the movie, never
    /// on the stale song the pre-migration session DTO would have named.
    /// </summary>
    [Fact]
    public async Task HandleAsync_StaleTokenVideoDisplacement_TogglesTheVideo()
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("fav-displace");
        var song = new Audio { Name = "Old Song", Id = Guid.NewGuid(), Path = "/music/o.mp3" };
        var movie = new Movie { Name = "Current Movie", Id = Guid.NewGuid(), Path = "/movies/c.mkv" };
        var data = SetupHappyPath(movie);
        string deviceId = "fav-displace-device";
        queueManager.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var handler = CreateHandler(queueManager);

        var response = await handler.HandleAsync(
            Request(), TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.True(data.IsFavorite, "the displaced video is marked favorite, not the stale song");
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.Is<BaseItem>(i => i.Id == movie.Id),
            It.IsAny<UserItemData>(),
            UserDataSaveReason.UpdateUserRating,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("added to favorites", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing resolvable anywhere (no token, no ledger, no session item): the
    /// NoMediaPlaying tell (JF-788, the guard families' shared door word), and
    /// no user-data write.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoResolvableItem_NoMediaPlayingWithoutWriting()
    {
        var handler = CreateHandler(TestHelpers.CreateDeviceQueueManager("fav-empty"));

        var response = await handler.HandleAsync(
            Request(), TestHelpers.CreateTestContext("fav-empty-device"),
            TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.Contains("Nothing is currently playing", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// JF-785 red proof (the migration leg): the full-item-without-DTO shape
    /// (held FullNowPlayingItem, no DTO, no token; the evidence legs are
    /// documented on HasCurrentPlaybackEvidence) resolves the held item and the
    /// toggle lands on it. RED on the pre-JF-785 tree: the DTO-only guard
    /// answered MediaNotFound here.
    /// </summary>
    [Fact]
    public async Task HandleAsync_FullItemHeldWithoutDto_TogglesHeldItem_JF785()
    {
        var movie = new Movie { Name = "Held Movie", Id = Guid.NewGuid(), Path = "/movies/held.mkv" };
        var data = SetupHappyPath(movie);
        var queues = TestHelpers.CreateDeviceQueueManager("fav-held-jf785");
        queues.RecordLastPlayed("fav-held-jf785-device", movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var handler = CreateHandler(queues);
        var session = _fx.CreateSession();
        session.FullNowPlayingItem = movie;
        Assert.Null(session.NowPlayingItem); // the full-item-WITHOUT-DTO shape

        var response = await handler.HandleAsync(
            Request(), TestHelpers.CreateTestContext("fav-held-jf785-device"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(data.IsFavorite, "the held full item is the toggle target");
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.Is<BaseItem>(i => i.Id == movie.Id),
            data,
            UserDataSaveReason.UpdateUserRating,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("added to favorites", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-785 Leg A (the unresolvable-evidence door; the resolver doc owns the
    /// contract): a now-playing DTO whose id does not resolve (Guid.Empty
    /// stands for the deleted-mid-play shape) plus a days-old ledger entry; the
    /// write must not land on the ledger item. RED on the pre-JF-785 tree: the
    /// tail flowed through and the days-old item was favorited.
    /// </summary>
    [Fact]
    public async Task HandleAsync_UnresolvableDtoStaleLedger_NoWrite_JF785()
    {
        var oldSong = new Audio { Name = "Days Old Song", Id = Guid.NewGuid(), Path = "/music/old.mp3" };
        var data = SetupHappyPath(oldSong); // user-data write must be REACHABLE, or the pin is vacuous
        var queues = TestHelpers.CreateDeviceQueueManager("fav-door-jf785");
        queues.RecordLastPlayed("fav-door-jf785-device", oldSong.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        var handler = CreateHandler(queues);
        var session = _fx.CreateSession();
        session.NowPlayingItem = new BaseItemDto { Id = Guid.Empty, Name = "Ghost Track" };

        var response = await handler.HandleAsync(
            Request(), TestHelpers.CreateTestContext("fav-door-jf785-device"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains("Nothing is currently playing", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.False(data.IsFavorite, "the days-old ledger item must not become the toggle target");
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Gate-marker tail F4, the deleted-mid-play shape the Guid.Empty twin
    /// cannot reach: a now-playing DTO carrying a REAL id that no longer
    /// resolves (GetItemById returns null; the fixture's loose mock answers
    /// null for the unsetup ghost id) flows through the resolver's DTO arm -
    /// the production path for a track deleted mid-play - and must not fall
    /// through to the days-old ledger tail.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DeletedMidPlayDtoStaleLedger_NoWrite_JF785()
    {
        var oldSong = new Audio { Name = "Days Old Song", Id = Guid.NewGuid(), Path = "/music/old.mp3" };
        var data = SetupHappyPath(oldSong); // user-data write must be REACHABLE, or the pin is vacuous
        var queues = TestHelpers.CreateDeviceQueueManager("fav-ghost-jf785");
        queues.RecordLastPlayed("fav-ghost-jf785-device", oldSong.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        var handler = CreateHandler(queues);
        var session = _fx.CreateSession();
        session.NowPlayingItem = new BaseItemDto { Id = Guid.NewGuid(), Name = "Deleted Mid-Play Track" };

        var response = await handler.HandleAsync(
            Request(), TestHelpers.CreateTestContext("fav-ghost-jf785-device"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains("Nothing is currently playing", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.False(data.IsFavorite, "the days-old ledger item must not become the toggle target");
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The JF-788 boundary pin: the data==null branch is NOT the evidence door
    /// (the item resolved and is playing; only its user-data row is missing), so
    /// it deliberately KEEPS MediaNotFound while both door branches above speak
    /// NoMediaPlaying. Pinned so a future wording-unification sweep cannot flip
    /// the keep as "the family's last MediaNotFound" without this test going red;
    /// JF-818 owns the open wording question for this branch.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ItemResolvedButNoUserData_KeepsMediaNotFound_JF788()
    {
        var song = new Audio { Name = "Dataless Song", Id = Guid.NewGuid(), Path = "/music/d.mp3" };
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _fx.UserDataManager.Setup(u => u.GetUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            Request(), TestHelpers.CreateContextWithToken(song.Id.ToString(), "fav-nodata-device"),
            TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.Contains("could not find the media", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
