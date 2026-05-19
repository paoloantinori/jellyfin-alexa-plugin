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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class DialogDelegationTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public DialogDelegationTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

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
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = CreateIntentRequest(IntentNames.PlaySong, "STARTED");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task PlaySong_WithSlots_ProcessesNormally()
    {
        var handler = new PlaySongIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = CreateIntentRequest(IntentNames.PlaySong, "COMPLETED",
            new Dictionary<string, string> { { "song", "Bohemian Rhapsody" }, { "musician", "Queen" } });
        var session = CreateSession();

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }

    [Fact]
    public async Task PlayAlbum_MissingSlot_ElicitsAlbumName()
    {
        var handler = new PlayAlbumIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = CreateIntentRequest(IntentNames.PlayAlbum, "STARTED");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task PlayAlbum_WithPartialSlots_ElicitsRemaining()
    {
        var handler = new PlayAlbumIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        // Album slot missing even though musician is provided
        var request = CreateIntentRequest(IntentNames.PlayAlbum, "IN_PROGRESS",
            new Dictionary<string, string> { { "musician", "Queen" } });
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task PlayEpisode_DoesNotDelegate()
    {
        var handler = new PlayEpisodeIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = CreateIntentRequest(IntentNames.PlayEpisode, "COMPLETED",
            new Dictionary<string, string>
            {
                { "series_name", "The Office" },
                { "season_number", "4" },
                { "episode_number", "10" }
            });
        var session = CreateSession();

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives ?? new List<IDirective>(), d => d.Type == "Dialog.Delegate");
    }

    [Fact]
    public async Task PlaySong_NullDialogState_ElicitsSongName()
    {
        var handler = new PlaySongIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = CreateIntentRequest(IntentNames.PlaySong, null);
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.Reprompt);
    }
}
