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
    private static Context CreateContextWithApl() => TestHelpers.CreateContextWithApl();

    private static Context CreateContextWithoutApl() => TestHelpers.CreateContextWithoutApl();

    /// <summary>
    /// Ensure APL visuals are enabled so "WithApl" tests pass regardless of
    /// static Plugin.Instance state left by other test classes running in parallel.
    /// </summary>
    private static void EnsureVisualsEnabled()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.AplVisualsEnabled = true;
        }
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
        EnsureVisualsEnabled();

        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
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
    public void BuildListDirective_WithEmptyList_ReturnsNull()
    {
        var directive = AplHelper.BuildListDirective("Test", new List<ListDisplayItem>(), "testList");

        Assert.Null(directive);
    }

    [Fact]
    public void BuildListDirective_WithItems_ReturnsDirective()
    {
        var items = new List<ListDisplayItem>
        {
            new("Song 1", "id-1", "Artist 1", "https://img.test/1.jpg"),
            new("Song 2", "id-2", "Artist 2", "https://img.test/2.jpg"),
            new("Song 3", "id-3") // no subtitle, no art
        };

        var directive = AplHelper.BuildListDirective("Soul Coughing Songs", items, "browseList");

        Assert.NotNull(directive);
        Assert.Equal("Alexa.Presentation.APL.RenderDocument", directive.Type);
        Assert.Equal("browseList", directive.Token);
        Assert.NotNull(directive.Document);
        Assert.NotNull(directive.DataSources);

        string dsStr = directive.DataSources.ToString();
        Assert.Contains("Song 1", dsStr);
        Assert.Contains("Artist 1", dsStr);
    }

    [Fact]
    public void BuildListDirective_Datasources_ContainTitleAndItems()
    {
        var items = new List<ListDisplayItem>
        {
            new("Track A", "audio-123", "Album X", "https://art/x.jpg")
        };

        var directive = AplHelper.BuildListDirective("My Library", items, "test");

        Assert.NotNull(directive);
        string dsStr = directive.DataSources!.ToString();
        Assert.Contains("My Library", dsStr);
        Assert.Contains("Track A", dsStr);
        Assert.Contains("Album X", dsStr);
        Assert.Contains("audio-123", dsStr);
    }

    [Fact]
    public void BuildListDirective_Document_ContainsSequenceWithDataBinding()
    {
        var items = new List<ListDisplayItem>
        {
            new("Item", "id-1")
        };

        var directive = AplHelper.BuildListDirective("Test", items, "test");

        Assert.NotNull(directive);
        var doc = directive.Document!;
        Assert.Equal("APL", doc["type"]!.ToString());
        Assert.Equal("1.7", doc["version"]!.ToString());
        Assert.Equal("dark", doc["theme"]!.ToString());

        // Verify data binding expressions are present
        string docStr = doc.ToString();
        Assert.Contains("${payload.listData.properties.title}", docStr);
        Assert.Contains("${payload.listData.properties.items}", docStr);
        Assert.Contains("${data.title}", docStr);
    }

    [Fact]
    public void BuildListDirective_ItemsWithOptionalFields_OmitSubtitleWhenNull()
    {
        var items = new List<ListDisplayItem>
        {
            new("Minimal Item", "min-id")
        };

        var directive = AplHelper.BuildListDirective("Test", items, "test");

        Assert.NotNull(directive);
        Assert.NotNull(directive.DataSources);
        string dsStr = directive.DataSources.ToString();
        Assert.Contains("Minimal Item", dsStr);
    }

    [Fact]
    public async Task BuildAudioPlayerResponse_WithoutAplContext_NoAplDirective()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
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

    [Fact]
    public async Task SearchMedia_Disambiguation_WithApl_IncludesAplDirective()
    {
        EnsureVisualsEnabled();

        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new SearchMediaIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object, userManagerMock.Object, loggerFactory);

        var audio1 = new Audio { Name = "Song A", Id = Guid.NewGuid() };
        audio1.Artists = new List<string> { "Artist 1" };
        var audio2 = new Audio { Name = "Song B", Id = Guid.NewGuid() };
        audio2.Artists = new List<string> { "Artist 2" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.SearchMedia,
                Slots = new Dictionary<string, Slot>
                {
                    ["query"] = new Slot { Value = "test song" }
                }
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        var context = CreateContextWithApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task SearchMedia_Disambiguation_WithoutApl_NoAplDirective()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new SearchMediaIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object, userManagerMock.Object, loggerFactory);

        var audio1 = new Audio { Name = "Song A", Id = Guid.NewGuid() };
        audio1.Artists = new List<string> { "Artist 1" };
        var audio2 = new Audio { Name = "Song B", Id = Guid.NewGuid() };
        audio2.Artists = new List<string> { "Artist 2" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.SearchMedia,
                Slots = new Dictionary<string, Slot>
                {
                    ["query"] = new Slot { Value = "test song" }
                }
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        var context = CreateContextWithoutApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task ListQueue_WithApl_IncludesAplDirective()
    {
        EnsureVisualsEnabled();

        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new ListQueueIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object, loggerFactory);

        var audio1 = new Audio { Name = "Song A", Id = Guid.NewGuid() };
        audio1.Artists = new List<string> { "Artist 1" };
        var audio2 = new Audio { Name = "Song B", Id = Guid.NewGuid() };
        audio2.Artists = new List<string> { "Artist 2" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.ListQueue,
                Slots = new Dictionary<string, Slot>()
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        libraryManagerMock.Setup(l => l.GetItemById(audio1.Id)).Returns(audio1);
        libraryManagerMock.Setup(l => l.GetItemById(audio2.Id)).Returns(audio2);

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        session.NowPlayingQueue = new List<MediaBrowser.Model.Session.QueueItem>
        {
            new() { Id = audio1.Id },
            new() { Id = audio2.Id }
        };

        var context = CreateContextWithApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task ListQueue_WithoutApl_NoAplDirective()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new ListQueueIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object, loggerFactory);

        var audio1 = new Audio { Name = "Song A", Id = Guid.NewGuid() };
        audio1.Artists = new List<string> { "Artist 1" };
        var audio2 = new Audio { Name = "Song B", Id = Guid.NewGuid() };
        audio2.Artists = new List<string> { "Artist 2" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.ListQueue,
                Slots = new Dictionary<string, Slot>()
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        libraryManagerMock.Setup(l => l.GetItemById(audio1.Id)).Returns(audio1);
        libraryManagerMock.Setup(l => l.GetItemById(audio2.Id)).Returns(audio2);

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        session.NowPlayingQueue = new List<MediaBrowser.Model.Session.QueueItem>
        {
            new() { Id = audio1.Id },
            new() { Id = audio2.Id }
        };

        var context = CreateContextWithoutApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task InProgressMediaList_WithApl_IncludesAplDirective()
    {
        EnsureVisualsEnabled();

        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var userDataManagerMock = new Mock<IUserDataManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new InProgressMediaListIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object,
            userManagerMock.Object, userDataManagerMock.Object, loggerFactory);

        var audio = new Audio { Name = "Halfway Song", Id = Guid.NewGuid() };
        audio.Artists = new List<string> { "Artist" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.InProgressMediaList,
                Slots = new Dictionary<string, Slot>()
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), audio))
            .Returns(new MediaBrowser.Controller.Entities.UserItemData
            {
                Key = "test",
                Played = false,
                PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks
            });

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        var context = CreateContextWithApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task InProgressMediaList_WithoutApl_NoAplDirective()
    {
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var userManagerMock = new Mock<IUserManager>();
        var userDataManagerMock = new Mock<IUserDataManager>();
        var config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new InProgressMediaListIntentHandler(
            sessionManagerMock.Object, config, libraryManagerMock.Object,
            userManagerMock.Object, userDataManagerMock.Object, loggerFactory);

        var audio = new Audio { Name = "Halfway Song", Id = Guid.NewGuid() };
        audio.Artists = new List<string> { "Artist" };

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.InProgressMediaList,
                Slots = new Dictionary<string, Slot>()
            },
            DialogState = "COMPLETED",
            Locale = "en-US"
        };

        userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), audio))
            .Returns(new MediaBrowser.Controller.Entities.UserItemData
            {
                Key = "test",
                Played = false,
                PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks
            });

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
        var context = CreateContextWithoutApl();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }
}
