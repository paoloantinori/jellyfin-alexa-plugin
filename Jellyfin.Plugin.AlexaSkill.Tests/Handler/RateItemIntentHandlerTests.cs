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
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for RateItemIntent (JF-326): the token-resolved happy path writes the
/// rating on Jellyfin UserItemData (stars * 2 on the 0-10 scale), the device
/// ledger backs up the VideoApp route, a stale audio token loses to a
/// VideoApp-routed ledger entry (the Repeat displacement shape), out-of-range
/// values are refused, and no-item/unparsable-slot branches answer cleanly.
/// </summary>
[Collection("Plugin")]
public class RateItemIntentHandlerTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private RateItemIntentHandler CreateHandler(DeviceQueueManager? queueManager = null)
    {
        return new RateItemIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.UserDataManager.Object,
            _fx.UserManager.Object,
            _fx.LibraryManager.Object,
            _fx.LoggerFactory,
            queueManager);
    }

    private static IntentRequest Request(string slotValue = "5", string locale = "en-US")
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Locale = locale,
            Intent = new Intent
            {
                Name = IntentNames.RateItem,
                Slots = new Dictionary<string, Slot>
                {
                    ["star_rating"] = new Slot { Name = "star_rating", Value = slotValue }
                }
            }
        };
    }

    private static Context ContextWithToken(string? token, string deviceId = "rate-tests-device")
        => TestHelpers.CreateContextWithToken(token, deviceId);

    /// <summary>
    /// The shared happy-path setup: the item resolves in the library, the
    /// Jellyfin user resolves, and GetUserData hands back a capturable
    /// UserItemData instance (the handler mutates it in place before saving).
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
    public void CanHandle_RateItemIntent_ReturnsTrue()
    {
        var handler = CreateHandler();

        Assert.True(handler.CanHandle(Request()));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.MarkFavorite }
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_TokenResolvesItem_WritesRatingOnTenScale()
    {
        var song = new Audio { Name = "Rhinelander", Id = Guid.NewGuid(), Path = "/music/r.mp3" };
        var data = SetupHappyPath(song);
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            Request("5"), ContextWithToken(song.Id.ToString()),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        // 5 spoken stars become 10.0 on Jellyfin's 0-10 UserItemData scale.
        Assert.Equal(10.0, data.Rating);
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            song,
            data,
            UserDataSaveReason.UpdateUserRating,
            It.IsAny<CancellationToken>()), Times.Once);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("5 out of 5 stars for Rhinelander", speech, StringComparison.Ordinal);
        Assert.True(response.Response.ShouldEndSession, "the confirmation is a Tell");
    }

    [Theory]
    [InlineData("1", 2.0)]
    [InlineData("3", 6.0)]
    public async Task HandleAsync_StarCounts_MapDoubledOntoTenScale(string spoken, double stored)
    {
        var song = new Audio { Name = "Map Test", Id = Guid.NewGuid(), Path = "/music/m.mp3" };
        var data = SetupHappyPath(song);
        var handler = CreateHandler();

        await handler.HandleAsync(
            Request(spoken), ContextWithToken(song.Id.ToString()),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Equal(stored, data.Rating);
    }

    /// <summary>
    /// The it-IT model's ItalianNumber slot delivers the Italian number word,
    /// not digits; the shared ItalianNumberWords parser must bridge it.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ItalianNumberWord_Parses()
    {
        var song = new Audio { Name = "Parola Test", Id = Guid.NewGuid(), Path = "/music/p.mp3" };
        var data = SetupHappyPath(song);
        var handler = CreateHandler();

        await handler.HandleAsync(
            Request("cinque", "it-IT"), ContextWithToken(song.Id.ToString()),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Equal(10.0, data.Rating);
    }

    /// <summary>
    /// The VideoApp route (seek-mode movies, episodes) never sets an
    /// AudioPlayer token; the device last-played ledger is the only signal
    /// there, so it must rate the ledger item.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoToken_LedgerFallbackRatesLedgerItem()
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("rate-ledger");
        var movie = new Movie { Name = "Ledger Movie", Id = Guid.NewGuid(), Path = "/movies/l.mkv" };
        var data = SetupHappyPath(movie);
        string deviceId = "rate-ledger-device";
        queueManager.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var handler = CreateHandler(queueManager);

        var response = await handler.HandleAsync(
            Request("4"), TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Equal(8.0, data.Rating);
        Assert.Contains("Ledger Movie", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Repeat displacement shape: the stale music token names the last
    /// SONG while a VideoApp-routed movie plays. The video is current, so the
    /// rating must land on the movie, never on the stale song.
    /// </summary>
    [Fact]
    public async Task HandleAsync_StaleTokenVideoDisplacement_RatesTheVideo()
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("rate-displace");
        var song = new Audio { Name = "Old Song", Id = Guid.NewGuid(), Path = "/music/o.mp3" };
        var movie = new Movie { Name = "Current Movie", Id = Guid.NewGuid(), Path = "/movies/c.mkv" };
        var data = SetupHappyPath(movie);
        _fx.LibraryManager.Setup(l => l.GetItemById(song.Id)).Returns(song);
        string deviceId = "rate-displace-device";
        queueManager.RecordLastPlayed(deviceId, movie.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var handler = CreateHandler(queueManager);

        await handler.HandleAsync(
            Request("2"), ContextWithToken(song.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Equal(4.0, data.Rating);
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// An audio-routed token-vs-ledger mismatch is the ordinary queue-advance
    /// shape (the ledger pinned the user-initiated play, the token then moved
    /// with the queue): the token's item wins.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AudioRouteMismatch_TokenWins()
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("rate-advance");
        var first = new Audio { Name = "First Track", Id = Guid.NewGuid(), Path = "/music/1.mp3" };
        var next = new Audio { Name = "Next Track", Id = Guid.NewGuid(), Path = "/music/2.mp3" };
        var data = SetupHappyPath(next);
        _fx.LibraryManager.Setup(l => l.GetItemById(first.Id)).Returns(first);
        string deviceId = "rate-advance-device";
        queueManager.RecordLastPlayed(deviceId, first.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        var handler = CreateHandler(queueManager);

        var response = await handler.HandleAsync(
            Request("3"), ContextWithToken(next.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Equal(6.0, data.Rating);
        Assert.Contains("Next Track", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// No token and no ledger: the session's now-playing item (the signal the
    /// favorite handlers read) is the final fallback.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NoTokenNoLedger_SessionNowPlayingFallback()
    {
        var song = new Audio { Name = "Session Song", Id = Guid.NewGuid(), Path = "/music/s.mp3" };
        var data = SetupHappyPath(song);
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.FullNowPlayingItem = song;
        var handler = CreateHandler(TestHelpers.CreateDeviceQueueManager("rate-session"));

        await handler.HandleAsync(
            Request("5"), TestHelpers.CreateTestContext("rate-session-device"),
            TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal(10.0, data.Rating);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("-1")]
    public async Task HandleAsync_OutOfRangeStars_RefusedWithoutWriting(string spoken)
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            Request(spoken), ContextWithToken(Guid.NewGuid().ToString()),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Contains("one to five stars", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// JF-549 shape: the empty-slot branch ASKS with the mic open via
    /// Dialog.ElicitSlot on star_rating, never a session-ending question.
    /// </summary>
    [Fact]
    public async Task HandleAsync_MissingSlot_ElicitsStarRating()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            Request(null!), TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        TestHelpers.AssertElicitsSlot(
            response, IntentNames.Slots.StarRating, IntentNames.RateItem,
            global::Jellyfin.Plugin.AlexaSkill.Alexa.Util.ElicitSlots.For(IntentNames.RateItem));
    }

    /// <summary>
    /// The elicitation-trap cancel hatch: while the elicit is open, a captured
    /// "stop" is a cancel, not a rating attempt.
    /// </summary>
    [Fact]
    public async Task HandleAsync_CapturedCancelWord_EndsSessionWithoutWriting()
    {
        var handler = CreateHandler();
        var request = Request("stop");
        request.DialogState = "IN_PROGRESS";

        var response = await handler.HandleAsync(
            request, TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(),
            d => d.Type == "Dialog.ElicitSlot");
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_NoResolvableItem_ApologizesWithoutWriting()
    {
        var handler = CreateHandler(TestHelpers.CreateDeviceQueueManager("rate-empty"));

        var response = await handler.HandleAsync(
            Request("5"), TestHelpers.CreateTestContext("rate-empty-device"),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Contains("nothing playing", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        _fx.UserDataManager.Verify(u => u.SaveUserData(
            It.IsAny<Jellyfin.Database.Implementations.Entities.User>(),
            It.IsAny<BaseItem>(),
            It.IsAny<UserItemData>(),
            It.IsAny<UserDataSaveReason>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
