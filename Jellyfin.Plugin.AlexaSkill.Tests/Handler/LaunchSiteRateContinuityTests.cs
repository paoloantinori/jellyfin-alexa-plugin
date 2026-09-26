using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-636 (code-review round): the launch sites OUTSIDE the speed handler must
/// CONTINUE the launch scope's rate instead of silently reverting an atempo
/// stream to 1x: the PlaybackNearlyFinished queue advance, the
/// PlaybackStarted next-track precompute, and the mid-play seek intents
/// (JumpToPosition, SkipForwardBack). Each fact seeds a rate-1500 launch scope
/// for the CURRENT item and asserts the issued/stored launch carries the atempo
/// URL at the same rate.
/// </summary>
[Collection("Plugin")]
public class LaunchSiteRateContinuityTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "rate-continuity-tests";

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;
    private readonly IDisposable _pluginQueueSwap;

    public LaunchSiteRateContinuityTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager("rate-continuity-tests", _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "rate-continuity-tests");
        _pluginQueueSwap = TestHelpers.SwapPluginQueueManager(_queueManager);

        // The seek handlers gate on Plugin.Instance's LIVE configuration
        // (IfFeatureDisabled); a shared collection instance may carry a stale
        // SeekEnabled from another suite, so set both refs explicitly.
        _fx.Config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;
    }

    public void Dispose()
    {
        _pluginQueueSwap.Dispose();
        _queueManager.Dispose();
        GC.SuppressFinalize(this);
    }

    private static long MinutesToTicks(double minutes) => TimeSpan.FromMinutes(minutes).Ticks;

    private static long MinutesToMs(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);

    private static Audio Track(string name) => new() { Name = name, Id = Guid.NewGuid(), RunTimeTicks = MinutesToTicks(30) };

    private Context ContextFor(BaseItem item, long offsetMs)
    {
        var context = TestHelpers.CreateTestContext(DeviceId);
        context.AudioPlayer = new PlaybackState
        {
            Token = item.Id.ToString(),
            OffsetInMilliseconds = offsetMs,
            PlayerActivity = "PLAYING"
        };
        return context;
    }

    private static AudioPlayerPlayDirective PlayDirective(SkillResponse response)
        => Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());

    private PlaybackNearlyFinishedEventHandler CreateNearlyFinishedHandler() => new(
        _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
        _fx.UserManager.Object, _fx.LoggerFactory, _queueManager);

    private PlaybackStartedEventHandler CreateStartHandler() => new(
        _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory, _fx.LibraryManager.Object);

    private static AudioPlayerRequest EventRequest(string type, Guid itemId, long offsetMs)
        => new() { Type = type, Token = itemId.ToString(), OffsetInMilliseconds = offsetMs };

    [Fact]
    public async Task QueueAdvance_ContinuesTheFinishingItemsAtempoRate()
    {
        Audio current = Track("Episode 1");
        Audio next = Track("Episode 2");
        _fx.LibraryManager.Setup(lm => lm.GetItemById(current.Id)).Returns(current);
        _fx.LibraryManager.Setup(lm => lm.GetItemById(next.Id)).Returns(next);
        _queueManager.SetQueue(DeviceId, new List<string> { current.Id.ToString(), next.Id.ToString() }, 0);
        _queueManager.RecordLaunchBase(DeviceId, current.Id.ToString(), 0, enqueued: false, ratePerMille: 1500);

        var session = CreateSession();
        session.FullNowPlayingItem = current;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = current.Id }, new() { Id = next.Id } };

        SkillResponse response = await CreateNearlyFinishedHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackNearlyFinished", current.Id, 0),
            ContextFor(current, 0),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // The enqueued successor launches at the SAME atempo rate (never 1x), from
        // its start (no ?start=), and the directive URL carries the launching
        // device hint (?d=) the endpoint's supersede-kill reads.
        var directive = PlayDirective(response);
        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);
        Assert.Contains($"/alexaskill/api/audio-speed/{next.Id}/1500/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Contains($"d={DeviceId}", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueAdvance_RatelessScope_KeepsThePlainLaunch()
    {
        Audio current = Track("Song 1");
        Audio next = Track("Song 2");
        _fx.LibraryManager.Setup(lm => lm.GetItemById(current.Id)).Returns(current);
        _fx.LibraryManager.Setup(lm => lm.GetItemById(next.Id)).Returns(next);
        _queueManager.SetQueue(DeviceId, new List<string> { current.Id.ToString(), next.Id.ToString() }, 0);
        _queueManager.RecordLaunchBase(DeviceId, current.Id.ToString(), 0, enqueued: false);

        var session = CreateSession();
        session.FullNowPlayingItem = current;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = current.Id }, new() { Id = next.Id } };

        SkillResponse response = await CreateNearlyFinishedHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackNearlyFinished", current.Id, 0),
            ContextFor(current, 0),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = PlayDirective(response);
        Assert.DoesNotContain("audio-speed", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrecomputeNext_StoresTheContinuedRate()
    {
        Audio current = Track("Episode 1");
        Audio next = Track("Episode 2");
        _fx.LibraryManager.Setup(lm => lm.GetItemById(current.Id)).Returns(current);
        _fx.LibraryManager.Setup(lm => lm.GetItemById(next.Id)).Returns(next);
        _fx.Config.PreEnqueueOnStart = true;
        _queueManager.RecordLaunchBase(DeviceId, current.Id.ToString(), 0, enqueued: false, ratePerMille: 1500);

        var session = CreateSession();
        session.FullNowPlayingItem = current;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = current.Id }, new() { Id = next.Id } };

        await CreateStartHandler().HandleAsync(
            EventRequest("AudioPlayer.PlaybackStarted", current.Id, 0),
            ContextFor(current, 0),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.True(NextTrackPrecomputeCache.TryGet(
            DeviceId, current.Id.ToString(), out Guid cachedId, out _, out string? cachedUrl, out int cachedRate));
        Assert.Equal(next.Id, cachedId);
        Assert.Equal(1500, cachedRate);
        Assert.Contains($"/alexaskill/api/audio-speed/{next.Id}/1500/stream.m3u8", cachedUrl, StringComparison.Ordinal);

        NextTrackPrecomputeCache.Invalidate(DeviceId);
    }

    [Fact]
    public async Task JumpToPosition_ContinuesTheAtempoRate()
    {
        Audio item = Track("Podcast episode");
        _fx.LibraryManager.Setup(lm => lm.GetItemById(item.Id)).Returns(item);
        _queueManager.RecordLaunchBase(DeviceId, item.Id.ToString(), 0, enqueued: false, ratePerMille: 1500);

        var session = CreateSession();
        session.FullNowPlayingItem = item;

        var handler = new JumpToPositionIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "JumpToPositionIntent",
                Slots = new Dictionary<string, Slot>
                {
                    ["position_minutes"] = new() { Name = "position_minutes", Value = "10" },
                },
            },
            Locale = "en-US",
            RequestId = "test-req",
        };

        SkillResponse response = await handler.HandleAsync(request, ContextFor(item, 0), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directive = PlayDirective(response);
        Assert.Contains($"/alexaskill/api/audio-speed/{item.Id}/1500/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Contains($"?start={TimeSpan.FromMinutes(10).Ticks}&", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task SkipForward_ContinuesTheAtempoRateFromTheContentPosition()
    {
        Audio item = Track("Podcast episode");
        _fx.LibraryManager.Setup(lm => lm.GetItemById(item.Id)).Returns(item);
        _queueManager.RecordLaunchBase(DeviceId, item.Id.ToString(), 0, enqueued: false, ratePerMille: 1500);

        var session = CreateSession();
        session.FullNowPlayingItem = item;
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = MinutesToTicks(30) };
        // PlayState is the CONTENT position (the event writers compose it): 10:00 in.
        session.PlayState = new PlayerStateInfo { PositionTicks = MinutesToTicks(10) };

        var handler = new SkipForwardBackIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "SkipForwardBackIntent",
                Slots = new Dictionary<string, Slot>
                {
                    ["seek_direction"] = new() { Name = "seek_direction", Value = "forward" },
                    ["seek_amount"] = new() { Name = "seek_amount", Value = "30" },
                    ["seek_unit"] = new() { Name = "seek_unit", Value = "seconds" },
                },
            },
            Locale = "en-US",
            RequestId = "test-req",
        };

        SkillResponse response = await handler.HandleAsync(request, ContextFor(item, 0), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var directive = PlayDirective(response);
        Assert.Contains($"/alexaskill/api/audio-speed/{item.Id}/1500/stream.m3u8", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        // 10:00 content + 30s skip = 10:30 content, carried as the ?start=.
        Assert.Contains($"?start={TimeSpan.FromMinutes(10.5).Ticks}&", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }
}
