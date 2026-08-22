#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-390: PreEnqueueOnStart eliminates the timing-dependent PlaybackNearlyFinished
/// round-trip by pre-enqueueing the next track when the current one STARTS playing.
/// When on, PlaybackStarted returns an AudioPlayer.Play (Enqueue) directive for the
/// next queue item instead of just a keep-alive ack.
/// </summary>
[Collection("Plugin")]
public class PreEnqueueOnStartTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PreEnqueueOnStartTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlaybackStartedEventHandler CreateHandler()
    {
        return new PlaybackStartedEventHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            _libraryManagerMock.Object);
    }

    private static AudioPlayerRequest CreateStartedRequest(string token, long offsetMs = 0)
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStarted",
            Token = token,
            OffsetInMilliseconds = offsetMs,
            RequestId = "test-req"
        };
    }

    private static Context CreateContext(string? token = null)
    {
        var context = TestHelpers.CreateTestContext();
        if (token != null)
        {
            context.AudioPlayer = new PlaybackState
            {
                Token = token,
                OffsetInMilliseconds = 0
            };
        }

        return context;
    }

    private SessionInfo CreateSession(List<QueueItem>? queue = null, Guid? currentItem = null)
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        if (queue != null)
        {
            session.NowPlayingQueue = queue;
        }

        if (currentItem.HasValue)
        {
            session.FullNowPlayingItem = new Audio { Name = "Current", Id = currentItem.Value };
        }

        return session;
    }

    private void SetupLibraryItem(Guid id, string name)
    {
        _libraryManagerMock.Setup(l => l.GetItemById(id))
            .Returns(new Audio { Name = name, Id = id });
    }

    // When the knob is OFF (default), PlaybackStarted returns a keep-alive ack
    // (existing behavior, unchanged).
    [Fact]
    public async Task PlaybackStarted_KnobOff_ReturnsKeepAlive()
    {
        _config.PreEnqueueOnStart = false;
        var handler = CreateHandler();
        var request = CreateStartedRequest(Guid.NewGuid().ToString());
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directives = response.Response?.Directives ?? new List<IDirective>();
        Assert.DoesNotContain(directives, d => d.Type == "AudioPlayer.Play");
    }

    // When the knob is ON and there is a next item in the queue, PlaybackStarted
    // returns an AudioPlayer.Play (Enqueue) directive for that item.
    [Fact]
    public async Task PlaybackStarted_KnobOn_NextInQueue_EnqueuesNextTrack()
    {
        _config.PreEnqueueOnStart = true;
        var currentId = Guid.NewGuid();
        var nextId = Guid.NewGuid();
        SetupLibraryItem(nextId, "Next Track");

        var handler = CreateHandler();
        var request = CreateStartedRequest(currentId.ToString());
        var session = CreateSession(
            new List<QueueItem>
            {
                new() { Id = currentId },
                new() { Id = nextId }
            },
            currentId);

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(currentId.ToString()), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.Contains(response.Response.Directives, d => d.Type == "AudioPlayer.Play");
    }

    // When the knob is ON but the current track is the LAST in the queue,
    // PlaybackStarted returns a keep-alive (nothing to pre-enqueue).
    [Fact]
    public async Task PlaybackStarted_KnobOn_LastInQueue_ReturnsKeepAlive()
    {
        _config.PreEnqueueOnStart = true;
        var currentId = Guid.NewGuid();
        var handler = CreateHandler();
        var request = CreateStartedRequest(currentId.ToString());
        var session = CreateSession(
            new List<QueueItem> { new() { Id = currentId } },
            currentId);

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(currentId.ToString()), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directives = response.Response?.Directives ?? new List<IDirective>();
        Assert.DoesNotContain(directives, d => d.Type == "AudioPlayer.Play");
    }

    // When the knob is ON but the queue is empty, keep-alive.
    [Fact]
    public async Task PlaybackStarted_KnobOn_EmptyQueue_ReturnsKeepAlive()
    {
        _config.PreEnqueueOnStart = true;
        var handler = CreateHandler();
        var request = CreateStartedRequest(Guid.NewGuid().ToString());
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(
            request, CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directives = response.Response?.Directives ?? new List<IDirective>();
        Assert.DoesNotContain(directives, d => d.Type == "AudioPlayer.Play");
    }
}
