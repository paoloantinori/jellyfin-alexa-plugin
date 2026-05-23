using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Test-only subclass that exposes the private protected static method for testing.
/// </summary>
internal class TestBaseHandler : BaseHandler
{
    public TestBaseHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => false;

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Task.FromResult(new SkillResponse());

    public static List<ListDisplayItem> CallGetRecentlyPlayedItems(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        PluginConfiguration config)
        => GetRecentlyPlayedItems(jellyfinUser, user, libraryManager, config);
}

public class GetRecentlyPlayedItemsTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public GetRecentlyPlayedItemsTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private static Jellyfin.Database.Implementations.Entities.User CreateJellyfinUser()
        => new("testuser", "test", "test");

    private static Entities.User CreatePluginUser()
        => TestHelpers.CreateTestUser(jellyfinToken: "test-token");

    [Fact]
    public void EmptyResult_WhenNoItemsExist()
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void DeduplicatesByItemName()
    {
        var songId1 = Guid.NewGuid();
        var songId2 = Guid.NewGuid();
        var audio1 = new Audio { Name = "Same Song", Id = songId1 };
        audio1.Artists = new List<string> { "Artist" };
        var audio2 = new Audio { Name = "Same Song", Id = songId2 };
        audio2.Artists = new List<string> { "Artist" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal(songId1.ToString(), result[0].Id);
    }

    [Fact]
    public void LimitsToTenItemsAfterDedup()
    {
        var items = new List<BaseItem>();
        for (int i = 0; i < 15; i++)
        {
            var audio = new Audio { Name = $"Song {i}", Id = Guid.NewGuid() };
            audio.Artists = new List<string> { $"Artist {i}" };
            items.Add(audio);
        }

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void AudioSubtitle_ContainsArtistAndAlbum()
    {
        var audio = new Audio { Name = "Test Song", Id = Guid.NewGuid(), Album = "Test Album" };
        audio.Artists = new List<string> { "Test Artist" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal("Test Artist · Test Album", result[0].Subtitle);
    }

    [Fact]
    public void EpisodeSubtitle_ContainsSeriesName()
    {
        var episode = new Episode { Name = "Pilot", Id = Guid.NewGuid(), SeriesName = "Test Series" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { episode });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal("Test Series", result[0].Subtitle);
    }

    [Fact]
    public void ItemsHaveCorrectTitleAndId()
    {
        var audioId = Guid.NewGuid();
        var audio = new Audio { Name = "My Song", Id = audioId };
        audio.Artists = new List<string> { "Artist" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal("My Song", result[0].Title);
        Assert.Equal(audioId.ToString(), result[0].Id);
    }

    [Fact]
    public void ItemsHaveArtUrl()
    {
        var audio = new Audio { Name = "Song", Id = Guid.NewGuid() };
        audio.Artists = new List<string> { "Artist" };
        var user = CreatePluginUser();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), user, _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.NotNull(result[0].ArtUrl);
        Assert.Contains(audio.Id.ToString(), result[0].ArtUrl);
        Assert.Contains(user.JellyfinToken, result[0].ArtUrl);
    }

    [Fact]
    public void SkipsItemsWithEmptyName()
    {
        var audio1 = new Audio { Name = "", Id = Guid.NewGuid() };
        var audio2 = new Audio { Name = "Valid Song", Id = Guid.NewGuid() };
        audio2.Artists = new List<string> { "Artist" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal("Valid Song", result[0].Title);
    }

    [Fact]
    public void SkipsItemsWithWhitespaceName()
    {
        var audio1 = new Audio { Name = "   ", Id = Guid.NewGuid() };
        var audio2 = new Audio { Name = "Valid Song", Id = Guid.NewGuid() };
        audio2.Artists = new List<string> { "Artist" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal("Valid Song", result[0].Title);
    }

    [Fact]
    public void EmptyResult_WhenAllMediaTypesDisabled()
    {
        _config.MusicEnabled = false;
        _config.VideosEnabled = false;
        _config.BooksEnabled = false;

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void MovieSubtitle_IsEmpty()
    {
        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Test Movie", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal(string.Empty, result[0].Subtitle);
    }

    [Fact]
    public void QueryUsesCorrectSortOrder()
    {
        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.NotNull(capturedQuery);
        Assert.Equal(20, capturedQuery.Limit);
        Assert.NotNull(capturedQuery.OrderBy);
        Assert.Single(capturedQuery.OrderBy);
        Assert.Equal(ItemSortBy.DatePlayed, capturedQuery.OrderBy[0].OrderBy);
        Assert.Equal(SortOrder.Descending, capturedQuery.OrderBy[0].SortOrder);
    }

    [Fact]
    public void DedupIsCaseInsensitive()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var audio1 = new Audio { Name = "Song Name", Id = id1 };
        audio1.Artists = new List<string> { "Artist" };
        var audio2 = new Audio { Name = "song name", Id = id2 };
        audio2.Artists = new List<string> { "Artist" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Single(result);
        Assert.Equal(id1.ToString(), result[0].Id);
    }

    [Fact]
    public void ReturnsEmptyList_WhenLibraryManagerReturnsNull()
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((IReadOnlyList<BaseItem>?)null);

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void MixedMediaTypes_AllReturned()
    {
        var audio = new Audio { Name = "Song", Id = Guid.NewGuid() };
        audio.Artists = new List<string> { "Artist" };
        audio.Album = "Album";
        var episode = new Episode { Name = "Episode 1", Id = Guid.NewGuid(), SeriesName = "Show" };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio, episode });

        var result = TestBaseHandler.CallGetRecentlyPlayedItems(
            CreateJellyfinUser(), CreatePluginUser(), _libraryManagerMock.Object, _config);

        Assert.Equal(2, result.Count);
        Assert.Equal("Artist · Album", result[0].Subtitle);
        Assert.Equal("Show", result[1].Subtitle);
    }
}
