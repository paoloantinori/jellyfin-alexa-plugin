using System;
using System.Collections.Generic;
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

public class PlayMoodMusicIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayMoodMusicIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayMoodMusicIntentHandler CreateHandler()
    {
        return new PlayMoodMusicIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? mood = null)
    {
        var intent = new Intent { Name = IntentNames.PlayMoodMusic };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (mood != null)
        {
            intent.Slots["mood"] = new global::Alexa.NET.Request.Slot { Name = "mood", Value = mood };
        }

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
    public void CanHandle_PlayMoodMusicIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");

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
    public async Task HandleAsync_MissingMood_ReturnsPrompt()
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
    public async Task HandleAsync_RelaxingMood_PlaysFromMappedGenres()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Chill Track", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_UnknownMood_TriesMoodAsGenre()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "funkyspacerock");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Space Rock", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task HandleAsync_NoMatchingMusic_ReturnsNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: "relaxing");
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

    [Theory]
    [InlineData("morning")]
    [InlineData("evening")]
    [InlineData("dinner")]
    [InlineData("workout")]
    public async Task HandleAsync_TimeOfDayMoods_PlaysFromMappedGenres(string mood)
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(mood: mood);
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Test Track", Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        _libraryManagerMock.Setup(l => l.GetItemById(audio.Id))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public void ResolveGenres_ExactMatch_ReturnsMappedGenres()
    {
        string[] genres = PlayMoodMusicIntentHandler.ResolveGenres("morning", hour: -1);
        Assert.Contains("acoustic", genres);
    }

    [Fact]
    public void ResolveGenres_TimeBiasMorning_ReordersGenres()
    {
        // "happy" maps to [pop, dance, reggae]. Morning bias (hour 7) prefers pop/folk/indie.
        // "pop" is in morning preferred list so it should come first.
        string[] morningBias = PlayMoodMusicIntentHandler.ResolveGenres("happy", hour: 7);
        Assert.Equal(3, morningBias.Length);
        Assert.Equal("pop", morningBias[0], ignoreCase: true);
    }

    [Fact]
    public void ResolveGenres_TimeBiasEvening_ReordersGenres()
    {
        // "relaxing" maps to [ambient, acoustic, jazz, classical, new age].
        // Evening bias (hour 20) prefers jazz, ambient, lounge, soul, classical.
        // Jazz and ambient are in both, so they should be at the front.
        string[] eveningBias = PlayMoodMusicIntentHandler.ResolveGenres("relaxing", hour: 20);
        Assert.Equal(5, eveningBias.Length);

        // Preferred genres (jazz, ambient, classical) should appear before non-preferred (acoustic, new age)
        int jazzIdx = Array.FindIndex(eveningBias, g => g.Equals("jazz", StringComparison.OrdinalIgnoreCase));
        int ambientIdx = Array.FindIndex(eveningBias, g => g.Equals("ambient", StringComparison.OrdinalIgnoreCase));
        int acousticIdx = Array.FindIndex(eveningBias, g => g.Equals("acoustic", StringComparison.OrdinalIgnoreCase));

        // Jazz and ambient should rank ahead of acoustic (not in evening preferred)
        Assert.True(jazzIdx < acousticIdx, "jazz should rank ahead of acoustic in evening bias");
        Assert.True(ambientIdx < acousticIdx, "ambient should rank ahead of acoustic in evening bias");
    }

    [Fact]
    public void ResolveGenres_TimeBiasAfternoon_PrefersHighEnergy()
    {
        // "upbeat" maps to [pop, rock, dance, electronic]. Afternoon (hour 15) prefers rock, electronic, hip hop, dance.
        // rock and electronic should be boosted ahead of pop.
        string[] afternoonBias = PlayMoodMusicIntentHandler.ResolveGenres("upbeat", hour: 15);
        Assert.Equal(4, afternoonBias.Length);

        int rockIdx = Array.FindIndex(afternoonBias, g => g.Equals("rock", StringComparison.OrdinalIgnoreCase));
        int electronicIdx = Array.FindIndex(afternoonBias, g => g.Equals("electronic", StringComparison.OrdinalIgnoreCase));
        int popIdx = Array.FindIndex(afternoonBias, g => g.Equals("pop", StringComparison.OrdinalIgnoreCase));

        Assert.True(rockIdx < popIdx, "rock should rank ahead of pop in afternoon bias");
        Assert.True(electronicIdx < popIdx, "electronic should rank ahead of pop in afternoon bias");
    }

    [Fact]
    public void ResolveGenres_UnknownMood_FallsBackToRawMood()
    {
        string[] genres = PlayMoodMusicIntentHandler.ResolveGenres("funkyspacerock", hour: 12);
        Assert.Single(genres);
        Assert.Equal("funkyspacerock", genres[0]);
    }
}
