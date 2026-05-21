using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for PlayBookIntentHandler resume behavior:
/// book progress detection, chapter skipping, and offset calculation.
/// </summary>
public class PlayBookResumeTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayBookResumeTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory, c => { }, "playbook-resume-tests");
    }

    private PlayBookIntentHandler CreateHandler()
    {
        return new PlayBookIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string bookName)
    {
        var intent = new global::Alexa.NET.Request.Intent { Name = IntentNames.PlayBook };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
        {
            ["book"] = new global::Alexa.NET.Request.Slot { Name = "book", Value = bookName }
        };

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Jellyfin.Plugin.AlexaSkill.Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private void SetupBookAndTracks(string bookName, List<Audio> tracks)
    {
        var bookItem = new Audio
        {
            Name = bookName,
            Id = Guid.NewGuid()
        };

        // Book search returns the book
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        // Track listing returns the tracks
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == bookItem.Id)))
            .Returns(new QueryResult<BaseItem>
            {
                Items = tracks.Cast<BaseItem>().ToList(),
                TotalRecordCount = tracks.Count
            });
    }

    [Fact]
    public async Task HandleAsync_BookWithProgress_ResumesFromCorrectChapter()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Hobbit");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // Create 5 tracks for the book
        var tracks = new List<Audio>();
        for (int i = 0; i < 5; i++)
        {
            tracks.Add(new Audio
            {
                Name = $"Chapter {i + 1}",
                Id = Guid.NewGuid()
            });
        }

        SetupBookAndTracks("The Hobbit", tracks);

        // Chapters 0 and 1 are played, chapter 2 has in-progress position at 45 seconds
        var playedData0 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        var playedData1 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        var inProgressData2 = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromSeconds(45).Ticks
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(playedData0);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[1]))
            .Returns(playedData1);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[2]))
            .Returns(inProgressData2);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[3]))
            .Returns((UserItemData?)null);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[4]))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // Should resume from chapter 3 (index 2) with offset 45 seconds
        Assert.Equal((int)TimeSpan.FromSeconds(45).TotalMilliseconds, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // The stream URL should contain the chapter 3 track's ID (tracks are sliced starting from the resume index)
        Assert.Contains(tracks[2].Id.ToString(), audioDirective.AudioItem.Stream.Url);

        // Output speech should indicate resumption
        Assert.NotNull(response.Response.OutputSpeech);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("resuming", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_BookWithNoProgress_StartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Hobbit");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var tracks = new List<Audio>
        {
            new() { Name = "Chapter 1", Id = Guid.NewGuid() },
            new() { Name = "Chapter 2", Id = Guid.NewGuid() },
            new() { Name = "Chapter 3", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("The Hobbit", tracks);

        // No progress on any track
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // Should start from the beginning with offset 0
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // Stream URL should contain the first track's ID
        Assert.Contains(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }

    [Fact]
    public async Task HandleAsync_BookAllPlayed_ResumesFromTrackAfterLastPlayed()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("The Hobbit");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var tracks = new List<Audio>
        {
            new() { Name = "Chapter 1", Id = Guid.NewGuid() },
            new() { Name = "Chapter 2", Id = Guid.NewGuid() },
            new() { Name = "Chapter 3", Id = Guid.NewGuid() },
            new() { Name = "Chapter 4", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("The Hobbit", tracks);

        // Chapters 0 and 1 are fully played, no in-progress chapter
        var playedData0 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        var playedData1 = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(playedData0);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[1]))
            .Returns(playedData1);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[2]))
            .Returns((UserItemData?)null);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[3]))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // Should resume from the track after the last played one (chapter 3, index 2)
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Contains(tracks[2].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }

    [Fact]
    public async Task HandleAsync_BookAllPlayed_NoMoreTracks_StartsFromBeginning()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Short Book");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        // Only one track, fully played
        var tracks = new List<Audio>
        {
            new() { Name = "Chapter 1", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("Short Book", tracks);

        var playedData = new UserItemData { Key = "test", Played = true, PlaybackPositionTicks = 0 };
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(playedData);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        // All played, no track after last played -> starts from beginning
        Assert.Equal(0, audioDirective.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Contains(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }

    [Fact]
    public async Task HandleAsync_BookWithProgress_StartsFromCorrectOffset()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest("Dune");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var tracks = new List<Audio>
        {
            new() { Name = "Part 1", Id = Guid.NewGuid() },
            new() { Name = "Part 2", Id = Guid.NewGuid() },
            new() { Name = "Part 3", Id = Guid.NewGuid() }
        };

        SetupBookAndTracks("Dune", tracks);

        // Part 1 has in-progress at 12 minutes 34 seconds
        var inProgressData = new UserItemData
        {
            Key = "test",
            Played = false,
            PlaybackPositionTicks = TimeSpan.FromMinutes(12).Ticks + TimeSpan.FromSeconds(34).Ticks
        };

        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[0]))
            .Returns(inProgressData);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[1]))
            .Returns((UserItemData?)null);
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), tracks[2]))
            .Returns((UserItemData?)null);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var audioDirective = response.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(audioDirective);

        int expectedOffsetMs = (int)(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(34)).TotalMilliseconds;
        Assert.Equal(expectedOffsetMs, audioDirective.AudioItem.Stream.OffsetInMilliseconds);

        // Should resume from Part 1 (index 0) since it is still in progress
        Assert.Contains(tracks[0].Id.ToString(), audioDirective.AudioItem.Stream.Url);
    }
}
