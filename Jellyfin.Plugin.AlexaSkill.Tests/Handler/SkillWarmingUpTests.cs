using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests that handlers respond with the SkillWarmingUp message when the artist
/// index is present but not ready (JF-419 cold-start after DLL deploy).
/// </summary>
[Collection("Plugin")]
public class SkillWarmingUpTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SkillWarmingUpTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private static IntentRequest CreateIntentRequest(string intentName, string? musician = null)
    {
        var intent = new Intent { Name = intentName };
        intent.Slots = new Dictionary<string, Slot>();
        if (musician != null)
        {
            intent.Slots["musician"] = new Slot { Name = "musician", Value = musician };
        }
        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession()
        => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
        => _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

    [Fact]
    public async Task PlayArtistSongs_IndexNotReady_RespondsWithWarmingMessage()
    {
        var handler = new PlayArtistSongsIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory,
            artistIndex: new WarmingArtistIndex(), queueManager: null);
        var request = CreateIntentRequest(IntentNames.PlayArtistSongs, "pink floyd");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.Contains("preparando", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaySong_IndexNotReady_RespondsWithWarmingMessage()
    {
        var handler = new PlaySongIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object,
            _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory,
            artistIndex: new WarmingArtistIndex());
        var request = CreateIntentRequest(IntentNames.PlaySong, "bohemian rhapsody");
        SetupUserMock();

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.Contains("preparando", speech, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A fake artist index that exists (non-null) but reports IsReady = false,
    /// simulating the cold-start loading window after a DLL deploy.
    /// </summary>
    private sealed class WarmingArtistIndex : IArtistIndex
    {
        public bool IsReady => false;
        public int Count => 0;
        public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null) => Array.Empty<BaseItem>();
        public bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes)
        {
            codes = default;
            return false;
        }
    }
}
