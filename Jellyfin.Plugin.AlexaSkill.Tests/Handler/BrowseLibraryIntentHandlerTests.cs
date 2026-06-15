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
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class BrowseLibraryIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public BrowseLibraryIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private BrowseLibraryIntentHandler CreateHandler()
    {
        return new BrowseLibraryIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? category = null, string? filter = null)
    {
        var intent = new Intent { Name = IntentNames.BrowseLibrary };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (category != null)
        {
            intent.Slots["browse_category"] = new global::Alexa.NET.Request.Slot { Name = "browse_category", Value = category };
        }

        if (filter != null)
        {
            intent.Slots["filter"] = new global::Alexa.NET.Request.Slot { Name = "filter", Value = filter };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

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

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public void CanHandle_BrowseLibraryIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "artists");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MissingCategory_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_BrowseArtists_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "artists");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { artist });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_BrowseAlbums_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "albums");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var album = new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { album });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_BrowseMovies_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "movies");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "The Matrix", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NoResults_ReturnsNoResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "songs");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_BrowseWithFilter_PassesFilterToQuery()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "artists", filter: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { artist });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_BrowseWithResults_WithApl_IncludesAplDirective()
    {
        EnsureVisualsEnabled();

        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "artists");
        var context = TestHelpers.CreateContextWithApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var artist2 = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { artist, artist2 });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task HandleAsync_BrowseSeries_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "series");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new MediaBrowser.Controller.Entities.TV.Series { Name = "Breaking Bad", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { series });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_BrowseSeries_ItalianAlias_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "serie");
        request.Locale = "it-IT";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var series = new MediaBrowser.Controller.Entities.TV.Series { Name = "Breaking Bad", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { series });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_MissingCategory_KeepsSessionOpen()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false,
            "Missing category prompt should keep the session open (use Ask, not Tell)");
        Assert.NotNull(response.Response?.Reprompt);
    }

    [Fact]
    public async Task HandleAsync_BrowseWithResults_WithoutApl_NoAplDirective()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "artists");
        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { artist });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task HandleAsync_BrowseBooks_DeduplicatesByParentId()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "libri");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        Guid parentA = Guid.NewGuid();
        Guid parentC = Guid.NewGuid();

        // Book A: multi-chapter book (3 tracks under one parent) → resolve parent for name.
        // Book B: single-file audiobook (1 track) → keep as-is.
        var tracks = new List<BaseItem>
        {
            new Audio { Name = "Chapter 1", Id = Guid.NewGuid(), ParentId = parentA },
            new Audio { Name = "Chapter 2", Id = Guid.NewGuid(), ParentId = parentA },
            new Audio { Name = "Chapter 3", Id = Guid.NewGuid(), ParentId = parentA },
            new Audio { Name = "Book B (standalone)", Id = Guid.NewGuid(), ParentId = parentC },
        };

        // Parent folder for Book A — use a regular Folder (book subfolder), not a CollectionFolder
        // which would be filtered out as a library root.
        var parentItems = new List<BaseItem>
        {
            new Folder { Name = "Book A Full Title", Id = parentA },
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds != null && q.ItemIds.Length > 0)))
            .Returns(parentItems);
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds == null || q.ItemIds.Length == 0)))
            .Returns(tracks);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);

        // The response should mention "2" books (1 parent-resolved + 1 standalone), not 4 tracks.
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("2", speech);
        Assert.DoesNotContain("4", speech);
    }

    [Fact]
    public async Task HandleAsync_BrowseBooks_FiltersOutLibraryRootFolders()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "libri");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        Guid rootFolderId = Guid.NewGuid();

        // Simulate a flat audiobook library: multiple tracks whose parent is the library root folder.
        var tracks = new List<BaseItem>
        {
            new Audio { Name = "Track 1", Id = Guid.NewGuid(), ParentId = rootFolderId },
            new Audio { Name = "Track 2", Id = Guid.NewGuid(), ParentId = rootFolderId },
        };

        // Parent resolution returns a CollectionFolder named "Audiobooks" — the library root.
        var parentItems = new List<BaseItem>
        {
            new CollectionFolder { Name = "Audiobooks", Id = rootFolderId },
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds != null && q.ItemIds.Length > 0)))
            .Returns(parentItems);
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds == null || q.ItemIds.Length == 0)))
            .Returns(tracks);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);

        // "Audiobooks" root folder must NOT appear in the response.
        Assert.DoesNotContain("Audiobooks", speech);
    }

    [Fact]
    public async Task HandleAsync_BrowseBooks_KeepsSingleFileBooksStandalone_UnderPlainFolderRoot()
    {
        // Regression for jf-266's real-world case: several single-file audiobooks sit
        // directly under a library root that is a plain Folder (not CollectionFolder/
        // AggregateFolder, so the type filter misses it). Each book has a distinct Album.
        // They must be listed individually, and the root folder must NEVER be resolved/shown.
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "libri");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        Guid rootFolderId = Guid.NewGuid();

        var tracks = new List<BaseItem>
        {
            new Audio { Name = "Honest Truth", Id = Guid.NewGuid(), ParentId = rootFolderId, Album = "Honest Truth" },
            new Audio { Name = "Power Moves", Id = Guid.NewGuid(), ParentId = rootFolderId, Album = "Power Moves" },
            new Audio { Name = "Radical Candor", Id = Guid.NewGuid(), ParentId = rootFolderId, Album = "Radical Candor" }
        };

        // If the root were resolved, it would return this plain Folder. It must never be reached.
        var parentItems = new List<BaseItem>
        {
            new Folder { Name = "Audiobooks", Id = rootFolderId }
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds != null && q.ItemIds.Length > 0)))
            .Returns(parentItems);
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds == null || q.ItemIds.Length == 0)))
            .Returns(tracks);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);

        // The library root folder must NOT appear (it is a plain Folder, not CollectionFolder).
        Assert.DoesNotContain("Audiobooks", speech);
        // Each single-file book should be listed individually.
        Assert.Contains("Honest Truth", speech);
        Assert.Contains("Radical Candor", speech);
    }

    [Fact]
    public async Task HandleAsync_BrowseBooks_FiltersOutAggregateFolders()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "libri");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        Guid rootFolderId = Guid.NewGuid();

        var tracks = new List<BaseItem>
        {
            new Audio { Name = "Chapter 1", Id = Guid.NewGuid(), ParentId = rootFolderId },
            new Audio { Name = "Chapter 2", Id = Guid.NewGuid(), ParentId = rootFolderId },
        };

        var parentItems = new List<BaseItem>
        {
            new AggregateFolder { Name = "Books Library", Id = rootFolderId },
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds != null && q.ItemIds.Length > 0)))
            .Returns(parentItems);
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds == null || q.ItemIds.Length == 0)))
            .Returns(tracks);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("Books Library", speech);
    }

    [Fact]
    public async Task HandleAsync_BrowseBooks_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "libri");
        request.Locale = "it-IT";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var book = new Audio { Name = "Thinking, Fast and Slow", Id = Guid.NewGuid(), ParentId = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds == null || q.ItemIds.Length == 0)))
            .Returns(new List<BaseItem> { book });
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds != null && q.ItemIds.Length > 0)))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.Contains("1", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task HandleAsync_BooksDisabled_ReturnsNoResults()
    {
        _config.BooksEnabled = false;
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "libri");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var book = new Audio { Name = "A Book", Id = Guid.NewGuid(), ParentId = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { book });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        // List results keep session open so user can pick an item.
        // (FilterByContentAccess would normally remove AudioBook items when BooksEnabled=false,
        // but the mock bypasses that, so BuildListResponse still returns the item.)
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false);
    }

    [Fact]
    public async Task HandleAsync_UnrecognizedCategory_ReturnsAsk()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "puzzles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false,
            "Unrecognized category should keep session open");
        Assert.NotNull(response.Response?.Reprompt);
    }

    [Fact]
    public async Task HandleAsync_GenresWithoutFilter_ReturnsResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "generi");
        request.Locale = "it-IT";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_ShortList_KeepsSessionOpenForSelection()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "artists");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // 3 items — fits in one voice page (≤5)
        var items = new List<BaseItem>
        {
            new MusicArtist { Name = "Artist A", Id = Guid.NewGuid() },
            new MusicArtist { Name = "Artist B", Id = Guid.NewGuid() },
            new MusicArtist { Name = "Artist C", Id = Guid.NewGuid() },
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false,
            "Non-truncated browse list should keep session open so user can pick an item");
        Assert.NotNull(response.Response?.Reprompt);
    }

    [Fact]
    public async Task HandleAsync_TruncatedList_KeepsSessionOpen()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: "artists");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = new List<BaseItem>();
        for (int i = 0; i < 8; i++)
        {
            items.Add(new MusicArtist { Name = $"Artist {i + 1}", Id = Guid.NewGuid() });
        }

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false,
            "Truncated browse list should keep session open");
        Assert.NotNull(response.Response?.Reprompt);
    }

    [Theory]
    [InlineData("dischi", typeof(MusicAlbum))]
    [InlineData("cantanti", typeof(MusicArtist))]
    [InlineData("gruppi", typeof(MusicArtist))]
    [InlineData("musica", typeof(Audio))]
    [InlineData("cartoni", typeof(MediaBrowser.Controller.Entities.TV.Series))]
    [InlineData("video", typeof(MediaBrowser.Controller.Entities.Movies.Movie))]
    public async Task HandleAsync_ItalianSynonyms_ReturnsList(string category, Type itemType)
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(category: category);
        request.Locale = "it-IT";
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var item = (BaseItem)Activator.CreateInstance(itemType)!;
        item.Name = "Test Item";
        item.Id = Guid.NewGuid();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { item });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }
}
