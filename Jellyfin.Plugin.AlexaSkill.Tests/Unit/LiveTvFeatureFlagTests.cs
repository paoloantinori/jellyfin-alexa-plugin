using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests that PlayChannelIntentHandler respects the LiveTvEnabled feature flag.
/// </summary>
[Collection("Plugin")]
public class LiveTvFeatureFlagTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public LiveTvFeatureFlagTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.LiveTvEnabled = _config.LiveTvEnabled,
            "alexa-livetv-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static IntentRequest CreateChannelRequest(string channelName)
    {
        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayChannelIntent",
                Slots = new Dictionary<string, Slot>
                {
                    { "channel", new Slot { Value = channelName } }
                }
            }
        };
    }

    [Fact]
    public async Task PlayChannel_ReturnsDisabledMessage_WhenLiveTvDisabled()
    {
        _config.LiveTvEnabled = false;
        Plugin.Instance!.Configuration.LiveTvEnabled = false;

        var handler = new PlayChannelIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, Mock.Of<ILiveTvStreamResolver>(), _loggerFactory);

        var response = await handler.HandleAsync(
            CreateChannelRequest("CNN"),
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayChannel_ProceedsNormally_WhenLiveTvEnabled()
    {
        var handler = new PlayChannelIntentHandler(
            _sessionManagerMock.Object, _config,
            _libraryManagerMock.Object, _userManagerMock.Object, Mock.Of<ILiveTvStreamResolver>(), _loggerFactory);

        // No channels in library — handler returns "couldn't find any channel"
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(Array.Empty<MediaBrowser.Controller.Entities.BaseItem>());

        var response = await handler.HandleAsync(
            CreateChannelRequest("CNN"),
            CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // Proceeds past the feature check — returns a user/login error, not "disabled"
        Assert.DoesNotContain("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }
}
