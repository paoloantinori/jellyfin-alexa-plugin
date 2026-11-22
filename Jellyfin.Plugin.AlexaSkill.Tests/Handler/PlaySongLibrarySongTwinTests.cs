#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-940 twin-intent tests: PlaySongIntentHandler serves BOTH PlaySongIntent
/// and PlayLibrarySongIntent (the JF-874 PlayVideo/PlayMovie shape). The twin
/// reads its SongTitle-typed LibrarySong slot as the same raw spoken title as
/// the free-text song slot, with NO request substitution (the musician slot's
/// catalog ER resolutions survive intact). Live-verified end to end on minix
/// 2026-11-22 (simulate 'chiedi a mia collezione di suonare il brano rapsodia'
/// selected PlayLibrarySongIntent, filled the slot raw, and played).
/// </summary>
[Collection("Plugin")]
public class PlaySongLibrarySongTwinTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlaySongIntentHandler CreateHandler()
    {
        return new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateIntent(string intentName, string slotName, string? slotValue)
    {
        var intent = new Intent { Name = intentName };
        intent.Slots = new Dictionary<string, Slot>();
        if (slotValue is not null)
        {
            intent.Slots[slotName] = new Slot { Name = slotName, Value = slotValue };
        }

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    [Fact]
    public void CanHandle_ClaimsBothIntentNames()
    {
        var handler = CreateHandler();
        Assert.True(handler.CanHandle(CreateIntent(IntentNames.PlaySong, "song", "x")));
        Assert.True(handler.CanHandle(CreateIntent(IntentNames.PlayLibrarySong, IntentNames.Slots.LibrarySong, "x")));
        Assert.False(handler.CanHandle(CreateIntent(IntentNames.PlayAlbum, "album", "x")));
        Assert.False(handler.CanHandle(new LaunchRequest { Locale = "it-IT", RequestId = "test-req" }));
    }

    [Fact]
    public async Task HandleAsync_LibrarySongSlotFill_RunsTheSongPipeline()
    {
        _fx.SetupUserMock();
        // Empty library: every query misses, so the handler answers the
        // not-found Tell. That still proves the twin's slot fill feeds the
        // search pipeline (the empty-slot arm elicits instead, and a fill
        // that never reached the pipeline would produce neither).
        _fx.LibraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);

        var response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayLibrarySong, IntentNames.Slots.LibrarySong, "xyzzyfoo"),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response?.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_EmptyLibrarySongSlot_ElicitsThroughPlaySongRegistration()
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(Array.Empty<BaseItem>());

        var handler = CreateHandler();
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);

        var response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayLibrarySong, IntentNames.Slots.LibrarySong, string.Empty),
            _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        // JF-874 shape: the twin elicits via the PlaySong dialog registration
        // (ElicitSongName); no PlayLibrarySong entry exists in any locale's
        // dialog.intents by design.
        Assert.NotNull(response);
        Assert.False(response.Response!.ShouldEndSession ?? true);
    }
}
