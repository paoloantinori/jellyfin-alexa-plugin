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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests that list-producing handlers correctly split voice vs APL display limits,
/// use partial locale keys when truncated, and keep the session open with ShowMorePrompt.
/// </summary>
public class ListTruncationTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public ListTruncationTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private static void EnsureVisualsEnabled()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.AplVisualsEnabled = true;
        }
    }

    private SessionInfo CreateSession()
        => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private static List<Audio> CreateAudioItems(int count)
    {
        var items = new List<Audio>();
        for (int i = 0; i < count; i++)
        {
            items.Add(new Audio { Name = $"Track {i + 1}", Id = Guid.NewGuid() });
        }

        return items;
    }

    // ================================================================
    // BrowseLibraryIntentHandler truncation tests
    // ================================================================

    [Fact]
    public async Task BrowseLibrary_ManyItems_UsesPartialKeyAndKeepsSessionOpen()
    {
        var handler = new BrowseLibraryIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.BrowseLibrary,
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["browse_category"] = new() { Name = "browse_category", Value = "artists" }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // 8 items exceeds voice limit (5), triggers partial key
        var artists = new List<BaseItem>();
        for (int i = 0; i < 8; i++)
        {
            artists.Add(new MusicArtist { Name = $"Artist {i + 1}", Id = Guid.NewGuid() });
        }

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(artists);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        // Should contain total count awareness
        Assert.Contains("8", speech);
        // Should contain "first 5" or similar partial indicator
        Assert.Contains("5", speech);
        // Should NOT contain items beyond voice limit
        Assert.DoesNotContain("Artist 6", speech);
        Assert.DoesNotContain("Artist 7", speech);
        Assert.DoesNotContain("Artist 8", speech);
        // Should contain show more prompt
        Assert.Contains("show more", speech, StringComparison.OrdinalIgnoreCase);
        // Session should be kept open (Ask, not Tell)
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false);
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task BrowseLibrary_FewItems_UsesFullKeyAndEndsSession()
    {
        var handler = new BrowseLibraryIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.BrowseLibrary,
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["browse_category"] = new() { Name = "browse_category", Value = "artists" }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // 3 items fits within voice limit
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Artist 1", Id = Guid.NewGuid() },
            new MusicArtist { Name = "Artist 2", Id = Guid.NewGuid() },
            new MusicArtist { Name = "Artist 3", Id = Guid.NewGuid() },
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(artists);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Artist 1", speech);
        Assert.Contains("Artist 3", speech);
        // No show more prompt when items fit
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        // Session ends normally
        Assert.True(response.Response.ShouldEndSession == true);
    }

    // ================================================================
    // QueryArtistLibraryIntentHandler truncation tests
    // ================================================================

    [Fact]
    public async Task QueryArtistLibrary_ManyTracks_UsesPartialKeyAndKeepsSessionOpen()
    {
        var handler = new QueryArtistLibraryIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.QueryArtistLibrary,
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["musician"] = new() { Name = "musician", Value = "Beatles" }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var tracks = CreateAudioItems(10);

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : tracks.Cast<BaseItem>().ToList();
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        // Voice reads first 5
        Assert.Contains("Track 1", speech);
        Assert.Contains("Track 5", speech);
        Assert.DoesNotContain("Track 6", speech);
        // Total count awareness
        Assert.Contains("10", speech);
        // Show more prompt
        Assert.Contains("show more", speech, StringComparison.OrdinalIgnoreCase);
        // Session open
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false);
    }

    [Fact]
    public async Task QueryArtistLibrary_FewTracks_UsesFullKeyAndEndsSession()
    {
        var handler = new QueryArtistLibraryIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.QueryArtistLibrary,
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["musician"] = new() { Name = "musician", Value = "Beatles" }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var tracks = CreateAudioItems(3);

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : tracks.Cast<BaseItem>().ToList();
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Track 1", speech);
        Assert.Contains("Track 3", speech);
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    [Fact]
    public async Task QueryArtistLibrary_ManyTracks_WithApl_SendsMoreItemsThanVoice()
    {
        EnsureVisualsEnabled();

        var handler = new QueryArtistLibraryIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.QueryArtistLibrary,
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["musician"] = new() { Name = "musician", Value = "Beatles" }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var tracks = CreateAudioItems(10);

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : tracks.Cast<BaseItem>().ToList();
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        // APL directive present
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
        // Voice still only reads 5
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("Track 6", speech);
    }

    // ================================================================
    // InProgressMediaListIntentHandler truncation tests
    // ================================================================

    [Fact]
    public async Task InProgressMedia_ManyItems_UsesPartialKeyAndKeepsSessionOpen()
    {
        var handler = new InProgressMediaListIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.InProgressMediaList },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // 8 items with progress
        var items = CreateAudioItems(8);
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        _userDataManagerMock.Setup(u => u.GetUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData
            {
                Key = "test",
                Played = false,
                PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        // Voice reads 5
        Assert.Contains("Track 1", speech);
        Assert.Contains("Track 5", speech);
        Assert.DoesNotContain("Track 6", speech);
        // Total count
        Assert.Contains("8", speech);
        // Show more prompt
        Assert.Contains("show more", speech, StringComparison.OrdinalIgnoreCase);
        // Session open
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false);
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task InProgressMedia_FewItems_UsesFullKeyAndEndsSession()
    {
        var handler = new InProgressMediaListIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.InProgressMediaList },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = CreateAudioItems(3);
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        _userDataManagerMock.Setup(u => u.GetUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData
            {
                Key = "test",
                Played = false,
                PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Track 1", speech);
        Assert.Contains("Track 3", speech);
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    [Fact]
    public async Task InProgressMedia_ExactlyFiveItems_NoTruncation()
    {
        var handler = new InProgressMediaListIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.InProgressMediaList },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = CreateAudioItems(5);
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        _userDataManagerMock.Setup(u => u.GetUserData(
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData
            {
                Key = "test",
                Played = false,
                PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Track 5", speech);
        // Exactly 5 items = no truncation
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    // ================================================================
    // ListQueueIntentHandler truncation tests
    // ================================================================

    [Fact]
    public async Task ListQueue_ManyUpcoming_UsesPartialKeyAndKeepsSessionOpen()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);

        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var queueIds = new List<Guid> { currentId };
        var audioItems = new List<Audio>();

        // Add 7 upcoming items after current
        for (int i = 0; i < 7; i++)
        {
            var id = Guid.NewGuid();
            queueIds.Add(id);
            audioItems.Add(new Audio { Id = id, Name = $"Queue Item {i + 1}" });
        }

        session.FullNowPlayingItem = new Audio { Id = currentId, Name = "Current" };
        session.NowPlayingQueue = queueIds.Select(id => new QueueItem { Id = id }).ToList();

        foreach (var audio in audioItems)
        {
            _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
                .Returns(audio);
        }

        var request = new IntentRequest
        {
            Intent = new Intent { Name = "ListQueueIntent" },
            Locale = "en-US",
            RequestId = "test-req"
        };

        SkillResponse response = await handler.HandleAsync(
            request, TestHelpers.CreateContextWithoutApl(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        // Voice reads first 5
        Assert.Contains("Queue Item 1", speech);
        Assert.Contains("Queue Item 5", speech);
        Assert.DoesNotContain("Queue Item 6", speech);
        // Total count awareness
        Assert.Contains("7", speech);
        // Show more prompt
        Assert.Contains("show more", speech, StringComparison.OrdinalIgnoreCase);
        // Session open
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false);
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task ListQueue_FewUpcoming_UsesFullKeyAndEndsSession()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);

        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        session.FullNowPlayingItem = new Audio { Id = currentId, Name = "Current" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = currentId },
            new() { Id = nextId }
        };

        _libraryManagerMock.Setup(l => l.GetItemById(nextId))
            .Returns(new Audio { Id = nextId, Name = "Next Song" });

        var request = new IntentRequest
        {
            Intent = new Intent { Name = "ListQueueIntent" },
            Locale = "en-US",
            RequestId = "test-req"
        };

        SkillResponse response = await handler.HandleAsync(
            request, TestHelpers.CreateContextWithoutApl(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Next Song", speech);
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    [Fact]
    public async Task ListQueue_ExactlyFiveUpcoming_NoTruncation()
    {
        var handler = new ListQueueIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _loggerFactory);

        var session = CreateSession();

        var currentId = Guid.NewGuid();
        var queueIds = new List<Guid> { currentId };
        var audioItems = new List<Audio>();

        for (int i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            queueIds.Add(id);
            audioItems.Add(new Audio { Id = id, Name = $"Queue Item {i + 1}" });
        }

        session.FullNowPlayingItem = new Audio { Id = currentId, Name = "Current" };
        session.NowPlayingQueue = queueIds.Select(id => new QueueItem { Id = id }).ToList();

        foreach (var audio in audioItems)
        {
            _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
                .Returns(audio);
        }

        var request = new IntentRequest
        {
            Intent = new Intent { Name = "ListQueueIntent" },
            Locale = "en-US",
            RequestId = "test-req"
        };

        SkillResponse response = await handler.HandleAsync(
            request, TestHelpers.CreateContextWithoutApl(), CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Queue Item 5", speech);
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    // ================================================================
    // QueryRecentlyAddedIntentHandler truncation tests
    // ================================================================

    [Fact]
    public async Task RecentlyAdded_ManyItems_KeepsSessionOpenWithShowMore()
    {
        var handler = new QueryRecentlyAddedIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.QueryRecentlyAdded },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // 8 items exceeds voice limit
        var items = new List<BaseItem>();
        for (int i = 0; i < 8; i++)
        {
            items.Add(new Audio
            {
                Name = $"New Track {i + 1}",
                Id = Guid.NewGuid(),
                Artists = new List<string> { $"Artist {i + 1}" }
            });
        }

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        // Voice reads first 5
        Assert.Contains("New Track 1", speech);
        Assert.Contains("New Track 5", speech);
        Assert.DoesNotContain("New Track 6", speech);
        Assert.DoesNotContain("New Track 7", speech);
        Assert.DoesNotContain("New Track 8", speech);
        // Show more prompt
        Assert.Contains("show more", speech, StringComparison.OrdinalIgnoreCase);
        // Session open
        Assert.True(response.Response.ShouldEndSession == null || response.Response.ShouldEndSession == false);
        Assert.NotNull(response.Response.Reprompt);
    }

    [Fact]
    public async Task RecentlyAdded_FewItems_EndsSessionNormally()
    {
        var handler = new QueryRecentlyAddedIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.QueryRecentlyAdded },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = new List<BaseItem>
        {
            new Audio { Name = "New Song", Id = Guid.NewGuid(), Artists = new List<string> { "Artist" } },
            new Movie { Name = "New Movie", Id = Guid.NewGuid() }
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("New Song", speech);
        Assert.Contains("New Movie", speech);
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    [Fact]
    public async Task RecentlyAdded_ExactlyFiveItems_NoTruncation()
    {
        var handler = new QueryRecentlyAddedIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.QueryRecentlyAdded },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = new List<BaseItem>();
        for (int i = 0; i < 5; i++)
        {
            items.Add(new Audio { Name = $"Item {i + 1}", Id = Guid.NewGuid() });
        }

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Item 5", speech);
        Assert.DoesNotContain("show more", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true);
    }

    [Fact]
    public async Task RecentlyAdded_ManyItems_WithApl_SendsMoreItemsThanVoice()
    {
        EnsureVisualsEnabled();

        var handler = new QueryRecentlyAddedIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.QueryRecentlyAdded },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var items = new List<BaseItem>();
        for (int i = 0; i < 8; i++)
        {
            items.Add(new Audio { Name = $"Item {i + 1}", Id = Guid.NewGuid() });
        }

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
        // Voice reads only 5
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("Item 6", speech);
    }

    // ================================================================
    // Config override tests
    // ================================================================

    [Fact]
    public async Task BrowseLibrary_ConfigMaxBrowseResults_RespectedInQuery()
    {
        _config.MaxBrowseResults = 25;

        var handler = new BrowseLibraryIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.BrowseLibrary,
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["browse_category"] = new() { Name = "browse_category", Value = "artists" }
                }
            },
            Locale = "en-US",
            RequestId = "test-req"
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQuery = q)
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The query limit should use MaxDisplayItems (from MaxListDisplayItems), not MaxBrowseResults
        Assert.NotNull(capturedQuery);
        Assert.Equal(15, capturedQuery.Limit); // Default MaxListDisplayItems = 15
    }
}
