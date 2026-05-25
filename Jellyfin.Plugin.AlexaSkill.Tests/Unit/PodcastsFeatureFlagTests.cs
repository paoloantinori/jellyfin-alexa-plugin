using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests that PlayPodcastIntentHandler respects the PodcastsEnabled feature flag.
/// </summary>
[Collection("Plugin")]
public class PodcastsFeatureFlagTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public PodcastsFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.PodcastsEnabled = _config.PodcastsEnabled,
            "alexa-podcasts-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static IntentRequest CreatePodcastRequest(string podcastName)
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayPodcastIntent",
                Slots = new Dictionary<string, Slot>
                {
                    { "podcast_name", new Slot { Value = podcastName } }
                }
            }
        };
    }

    [Fact]
    public async Task PlayPodcast_ReturnsDisabledMessage_WhenPodcastsDisabled()
    {
        _config.PodcastsEnabled = false;
        Plugin.Instance!.Configuration.PodcastsEnabled = false;

        var handler = new PlayPodcastIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var response = await handler.HandleAsync(
            CreatePodcastRequest("Some Podcast"),
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayPodcast_ProceedsNormally_WhenPodcastsEnabled()
    {
        var handler = new PlayPodcastIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        // No podcasts in library — handler returns "couldn't find a podcast"
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(Array.Empty<MediaBrowser.Controller.Entities.BaseItem>());

        var response = await handler.HandleAsync(
            CreatePodcastRequest("Test Podcast"),
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Proceeds past the feature check — returns a user/login error, not "disabled"
        Assert.DoesNotContain("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }
}
