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
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class AplHelperTests
{
    private static Context CreateContextWithApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>
                    {
                        { "Alexa.Presentation.APL", new { } }
                    }
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    private static Context CreateContextWithoutApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    [Fact]
    public void DeviceSupportsApl_WithAplInterface_ReturnsTrue()
    {
        var context = CreateContextWithApl();
        Assert.True(AplHelper.DeviceSupportsApl(context));
    }

    [Fact]
    public void DeviceSupportsApl_WithoutAplInterface_ReturnsFalse()
    {
        var context = CreateContextWithoutApl();
        Assert.False(AplHelper.DeviceSupportsApl(context));
    }

    [Fact]
    public void DeviceSupportsApl_NullContext_ReturnsFalse()
    {
        Context? context = null;
        Assert.False(AplHelper.DeviceSupportsApl(context!));
    }

    [Fact]
    public void BuildNowPlayingDirective_WithAudioItem_ReturnsDirective()
    {
        var audio = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid(),
            Album = "Test Album"
        };
        audio.Artists = new List<string> { "Test Artist" };

        var directive = AplHelper.BuildNowPlayingDirective(audio, "https://img.test/art.jpg", "https://img.test/bg.jpg");

        Assert.NotNull(directive);
        Assert.Equal("Alexa.Presentation.APL.RenderDocument", directive.Type);
        Assert.Equal("nowPlaying", directive.Token);
        Assert.NotNull(directive.Document);
        Assert.NotNull(directive.DataSources);
    }

    [Fact]
    public void BuildNowPlayingDirective_WithEmptyName_ReturnsNull()
    {
        var audio = new Audio { Name = string.Empty, Id = Guid.NewGuid() };

        var directive = AplHelper.BuildNowPlayingDirective(audio, "https://img.test/art.jpg", "https://img.test/bg.jpg");

        Assert.Null(directive);
    }

    [Fact]
    public void BuildQueueDirective_WithEmptyList_ReturnsNull()
    {
        var directive = AplHelper.BuildQueueDirective(new List<QueueDisplayItem>());

        Assert.Null(directive);
    }

    [Fact]
    public void BuildQueueDirective_WithItems_ReturnsDirective()
    {
        var items = new List<QueueDisplayItem>
        {
            new() { Title = "Song 1", Artist = "Artist 1", ArtUrl = "https://img.test/1.jpg" },
            new() { Title = "Song 2", Artist = "Artist 2", ArtUrl = "https://img.test/2.jpg" }
        };

        var directive = AplHelper.BuildQueueDirective(items);

        Assert.NotNull(directive);
        Assert.Equal("Alexa.Presentation.APL.RenderDocument", directive.Type);
        Assert.Equal("queue", directive.Token);
        Assert.NotNull(directive.Document);
        Assert.NotNull(directive.DataSources);
    }

    [Fact]
    public async Task BuildAudioPlayerResponse_WithAplContext_IncludesAplDirective()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new PlaySongIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object, userManagerMock.Object, loggerFactory);

        var audio = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        audio.Artists = new List<string> { "Artist" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.PlaySong,
                Slots = new Dictionary<string, Slot>
                {
                    ["song"] = new Slot { Value = "Test Song" },
                    ["musician"] = new Slot { Value = "Artist" }
                }
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        var context = CreateContextWithApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        // Should have both AudioPlayer.Play and APL RenderDocument directives
        Assert.Equal(2, response.Response.Directives.Count);
        Assert.Contains(response.Response.Directives, d => d.Type == "AudioPlayer.Play");
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task BuildAudioPlayerResponse_WithoutAplContext_NoAplDirective()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new PlaySongIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object, userManagerMock.Object, loggerFactory);

        var audio = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        audio.Artists = new List<string> { "Artist" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.PlaySong,
                Slots = new Dictionary<string, Slot>
                {
                    ["song"] = new Slot { Value = "Test Song" },
                    ["musician"] = new Slot { Value = "Artist" }
                }
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        var context = CreateContextWithoutApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        // Should have only AudioPlayer.Play directive
        Assert.Single(response.Response.Directives);
        Assert.Equal("AudioPlayer.Play", response.Response.Directives[0].Type);
    }
}
