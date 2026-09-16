using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using PostPlayBehavior = Jellyfin.Plugin.AlexaSkill.Configuration.PostPlayBehavior;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch 10 characterization suite for the progress-reporting family
/// (ComposeItemAbsolutePosition, ComposeEventPositionTicks,
/// TryGetRuntimeTicksForGuard, GetPostPlayBehavior; Alexa/Handler/ProgressReporter.cs).
/// The composition pair had only INDIRECT coverage before the move
/// (PlaybackPositionProvenanceTests drives it through the event handlers) and
/// GetPostPlayBehavior had none at all (its resolution was tested by a logic
/// mirror in PostPlayBehaviorTests, not against the member; that mirror was
/// replaced by these direct facts), so these facts pin the direct contracts
/// BEFORE the move: the resolution table, the base-0/raw passthrough, the
/// strictly-past-runtime guard (raw wins) vs the exactly-at-runtime admission
/// (composed stays), the Guid.Empty passthrough, and the fail-open runtime
/// lookup. Written green on the pre-move BaseHandler code via a probe subclass,
/// then retargeted to direct ProgressReporter construction after the move
/// (zero expectation edits).
/// </summary>
[Collection("Plugin")]
public class ProgressReporterTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "progress-reporter-tests";

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;

    public ProgressReporterTests()
    {
        _queueManager = TestHelpers.CreateDeviceQueueManager(
            DeviceId, _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        GC.SuppressFinalize(this);
    }

    private ProgressReporter CreateReporter()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LoggerFactory.CreateLogger<ProgressReporter>(),
            TestHelpers.CreateLaunchBuilder(_fx.Config));

    private static long MinutesToTicks(double minutes) => TimeSpan.FromMinutes(minutes).Ticks;

    private static long MinutesToMs(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    // ---- GetPostPlayBehavior: per-user override -> global default ----

    [Fact]
    public void GetPostPlayBehavior_NullUser_ReturnsGlobalDefault()
    {
        _fx.Config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;

        Assert.Equal(PostPlayBehavior.AutoPlay, CreateReporter().GetPostPlayBehavior(null));
    }

    [Fact]
    public void GetPostPlayBehavior_NullOverride_ReturnsGlobalDefault()
    {
        _fx.Config.DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay;
        var user = TestHelpers.CreateTestUser();
        user.PostPlayBehavior = null;

        Assert.Equal(PostPlayBehavior.AutoPlay, CreateReporter().GetPostPlayBehavior(user));
    }

    [Fact]
    public void GetPostPlayBehavior_UserOverride_WinsOverGlobal()
    {
        _fx.Config.DefaultPostPlayBehavior = PostPlayBehavior.Stop;
        var user = TestHelpers.CreateTestUser();
        user.PostPlayBehavior = PostPlayBehavior.AutoPlay;

        Assert.Equal(PostPlayBehavior.AutoPlay, CreateReporter().GetPostPlayBehavior(user));
    }

    [Fact]
    public void GetPostPlayBehavior_FreshConfig_DefaultsToStop()
        => Assert.Equal(PostPlayBehavior.Stop, CreateReporter().GetPostPlayBehavior(null));

    // ---- ComposeItemAbsolutePosition: the raw-to-item-absolute conversion ----

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Compose_NonPositiveBase_ReturnsRawTicksUnchanged(long baseMs)
    {
        long raw = MinutesToTicks(5);

        Assert.Equal(raw, CreateReporter().ComposeItemAbsolutePosition(raw, baseMs));
    }

    [Fact]
    public void Compose_PositiveBase_AddsBaseToRaw()
    {
        long raw = MinutesToTicks(5);

        Assert.Equal(MinutesToTicks(25), CreateReporter().ComposeItemAbsolutePosition(raw, MinutesToMs(20)));
    }

    [Fact]
    public void Compose_StrictlyPastRuntime_FallsBackToRaw()
    {
        // composed 30min > runtime 25min: the stale-base shape; the raw offset wins.
        long raw = MinutesToTicks(10);

        Assert.Equal(
            raw,
            CreateReporter().ComposeItemAbsolutePosition(raw, MinutesToMs(20), MinutesToTicks(25)));
    }

    [Fact]
    public void Compose_ExactlyAtRuntime_StaysComposed()
    {
        // composed 25min == runtime 25min: a legitimate finish-event composition; admitted.
        long raw = MinutesToTicks(5);

        Assert.Equal(
            MinutesToTicks(25),
            CreateReporter().ComposeItemAbsolutePosition(raw, MinutesToMs(20), MinutesToTicks(25)));
    }

    [Fact]
    public void Compose_UnknownRuntime_GuardSkipped()
    {
        long raw = MinutesToTicks(10);

        Assert.Equal(
            MinutesToTicks(30),
            CreateReporter().ComposeItemAbsolutePosition(raw, MinutesToMs(20), null));
    }

    // ---- ComposeEventPositionTicks: the event-writer entry point ----

    [Fact]
    public void ComposeEvent_EmptyItemId_PassesRawTicksThrough()
    {
        // An unparseable token has no scope: no launch lookup, raw preserved.
        Assert.Equal(
            TimeSpan.FromMilliseconds(30_000).Ticks,
            CreateReporter().ComposeEventPositionTicks(DeviceId, Guid.Empty, 30_000, "test", _queueManager, _fx.LibraryManager.Object));
    }

    [Fact]
    public void ComposeEvent_WithActiveLaunchBase_ComposesItemAbsolute()
    {
        var id = Guid.NewGuid();
        var track = new MediaBrowser.Controller.Entities.Audio.Audio { Id = id, Name = "Track", RunTimeTicks = MinutesToTicks(60) };
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(track);
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        Assert.Equal(
            MinutesToTicks(25),
            CreateReporter().ComposeEventPositionTicks(DeviceId, id, MinutesToMs(5), "test", _queueManager, _fx.LibraryManager.Object));
    }

    [Fact]
    public void ComposeEvent_RuntimeLookupThrows_FailOpen_ComposesAnyway()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Throws(new InvalidOperationException("db down"));
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false);

        // The guard is advisory: a failing runtime lookup must not kill the writer;
        // the composition persists without the guard.
        Assert.Equal(
            MinutesToTicks(25),
            CreateReporter().ComposeEventPositionTicks(DeviceId, id, MinutesToMs(5), "test", _queueManager, _fx.LibraryManager.Object));
    }

    // ---- TryGetRuntimeTicksForGuard: the fail-open runtime lookup ----

    [Fact]
    public void TryGetRuntime_NullLibrary_ReturnsNull()
        => Assert.Null(CreateReporter().TryGetRuntimeTicksForGuard(null, Guid.NewGuid()));

    [Fact]
    public void TryGetRuntime_EmptyItem_ReturnsNull()
        => Assert.Null(CreateReporter().TryGetRuntimeTicksForGuard(_fx.LibraryManager.Object, Guid.Empty));

    [Fact]
    public void TryGetRuntime_ItemWithRuntime_ReturnsTicks()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id))
            .Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Id = id, RunTimeTicks = MinutesToTicks(90) });

        Assert.Equal(MinutesToTicks(90), CreateReporter().TryGetRuntimeTicksForGuard(_fx.LibraryManager.Object, id));
    }

    [Fact]
    public void TryGetRuntime_Throws_ReturnsNull()
    {
        _fx.LibraryManager.Setup(lm => lm.GetItemById(It.IsAny<Guid>())).Throws(new InvalidOperationException("db down"));

        Assert.Null(CreateReporter().TryGetRuntimeTicksForGuard(_fx.LibraryManager.Object, Guid.NewGuid()));
    }
}
