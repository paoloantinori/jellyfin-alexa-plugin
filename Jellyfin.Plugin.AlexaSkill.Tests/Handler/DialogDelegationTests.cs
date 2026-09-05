using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
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
public class DialogDelegationTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture(configure: c => c.AsrCompoundWordFixEnabled = false);

    private static IntentRequest CreateIntentRequest(string intentName, string? dialogState, Dictionary<string, string?>? slots = null)
    {
        var intent = new Intent { Name = intentName };
        intent.Slots = new Dictionary<string,global::Alexa.NET.Request.Slot>();

        // Pre-populate expected slots so handlers can access them via indexer
        string[][] expectedSlots = intentName switch
        {
            "PlaySongIntent" => new[] { new[] { "song", "musician" } },
            "PlayAlbumIntent" => new[] { new[] { "album", "musician" } },
            _ => Array.Empty<string[]>()
        };

        foreach (var slotGroup in expectedSlots)
        {
            foreach (var slotName in slotGroup)
            {
                string? value = slots?.GetValueOrDefault(slotName);
                intent.Slots[slotName] = new global::Alexa.NET.Request.Slot { Name = slotName, Value = value };
            }
        }

        if (slots != null)
        {
            foreach (var kvp in slots)
            {
                intent.Slots[kvp.Key] = new global::Alexa.NET.Request.Slot { Name = kvp.Key, Value = kvp.Value };
            }
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req", DialogState = dialogState };
    }

    [Fact]
    public async Task PlaySong_MissingSlot_ElicitsSongName()
    {
        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        var request = CreateIntentRequest(IntentNames.PlaySong, "STARTED");
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task PlaySong_WithSlots_ProcessesNormally()
    {
        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        var request = CreateIntentRequest(IntentNames.PlaySong, "COMPLETED",
            new Dictionary<string, string> { { "song", "Bohemian Rhapsody" }, { "musician", "Queen" } });
        var session = _fx.CreateSession();

        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }

    [Fact]
    public async Task PlayAlbum_MissingSlot_ElicitsAlbumName()
    {
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        var request = CreateIntentRequest(IntentNames.PlayAlbum, "STARTED");
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task PlayAlbum_WithPartialSlots_ResolvesAlbumByArtist_NoDelegation()
    {
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        // Album slot missing even though musician is provided: JF-422 routes this into
        // the album-by-artist resolution (play without a title) instead of eliciting.
        // Catalog via the shared fixture mock (JF-442); the play/resolution outcome of
        // this exact scenario is pinned by the strictly stronger
        // PlayAlbumIntentHandlerTests.HandleAsync_DialogInProgressWithMusician_PlaysArtistsAlbum_NoTitlePrompt,
        // so this file keeps only its own angle: the response must not delegate the
        // dialog back to Alexa.
        var request = CreateIntentRequest(IntentNames.PlayAlbum, "IN_PROGRESS",
            new Dictionary<string, string> { { "musician", "Queen" } });
        var session = _fx.CreateSession();

        _fx.SetupUserMock();
        var artist = new MusicArtist { Name = "Queen", Id = Guid.NewGuid() };
        var album = new MusicAlbum { Name = "A Night at the Opera", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid(), ParentId = album.Id, Album = album.Name };
        _fx.SetupIndefiniteAlbumCatalog(
            artist,
            new List<BaseItem> { album },
            new List<BaseItem> { track },
            new Dictionary<Guid, BaseItem> { [album.Id] = track });

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }

    [Fact]
    public async Task PlayEpisode_DoesNotDelegate()
    {
        var handler = new PlayEpisodeIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.TvSeriesManager.Object, _fx.LoggerFactory);
        var request = CreateIntentRequest(IntentNames.PlayEpisode, "COMPLETED",
            new Dictionary<string, string>
            {
                { "series_name", "The Office" },
                { "season_number", "4" },
                { "episode_number", "10" }
            });
        var session = _fx.CreateSession();

        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }

    [Fact]
    public async Task PlaySong_NullDialogState_ElicitsSongName()
    {
        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);
        var request = CreateIntentRequest(IntentNames.PlaySong, null);
        var session = _fx.CreateSession();

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.Reprompt);
    }
}
