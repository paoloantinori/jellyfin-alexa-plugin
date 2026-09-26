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

    // ---- ComposeItemAbsolutePosition: the JF-636 atempo rate ----

    /// <summary>
    /// The load-bearing JF-636 composition: on an atempo stream the raw device
    /// offset counts the rate-adjusted OUTPUT timeline, so it scales by the rate
    /// BEFORE the base composes (content = base + raw x R).
    /// </summary>
    [Fact]
    public void Compose_RateAdjustedStream_ScalesBeforeAddingBase()
    {
        // base 20:00, raw 5:00 of stream at 1.5x = 7:30 of content -> 27:30.
        long raw = MinutesToTicks(5);
        long composed = CreateReporter().ComposeItemAbsolutePosition(
            raw, MinutesToMs(20), MinutesToTicks(60), "RateCompose", ratePerMille: 1500);

        Assert.Equal(MinutesToTicks(27.5), composed);
    }

    /// <summary>A from-zero atempo launch (base 0, rate 2x) still converts its offsets.</summary>
    [Fact]
    public void Compose_RateAdjustedStreamZeroBase_ScalesTheRawOffset()
    {
        long raw = MinutesToTicks(30);
        long composed = CreateReporter().ComposeItemAbsolutePosition(
            raw, 0, MinutesToTicks(60), "RateCompose", ratePerMille: 2000);

        Assert.Equal(MinutesToTicks(60), composed);
    }

    /// <summary>Rate 1000 is the identity: the pre-JF-636 arithmetic, byte-identical.</summary>
    [Fact]
    public void Compose_IdentityRate_KeepsTheClassicArithmetic()
    {
        long raw = MinutesToTicks(5);
        long composed = CreateReporter().ComposeItemAbsolutePosition(
            raw, MinutesToMs(20), MinutesToTicks(60), "RateCompose", ratePerMille: 1000);

        Assert.Equal(MinutesToTicks(25), composed);
    }

    /// <summary>
    /// The stale-scope guard still bounds the SCALED composition: a base + raw x rate
    /// strictly past the runtime persists the RATE-ADJUSTED offset WITHOUT the base
    /// as the conservative truth (JF-636 review: the unscaled raw offset is not in
    /// content units on an atempo stream).
    /// </summary>
    [Fact]
    public void Compose_RateAdjustedCompositionPastRuntime_PersistsScaledWithoutBase()
    {
        // base 50:00, raw 20:00 of stream at 2x = 40:00 content -> 90:00 > runtime 60.
        long raw = MinutesToTicks(20);
        long composed = CreateReporter().ComposeItemAbsolutePosition(
            raw, MinutesToMs(50), MinutesToTicks(60), "RateCompose", ratePerMille: 2000);

        Assert.Equal(MinutesToTicks(40), composed);
    }

    /// <summary>
    /// The 0.75x inversion the JF-636 review caught: below 1x the unscaled raw
    /// offset is LARGER than the composition being guarded (raw = scaled / 0.75),
    /// so the guard must return the SCALED value, never the raw one (persisting
    /// raw would write a past-runtime position: the anomaly the guard exists to
    /// suppress).
    /// </summary>
    [Fact]
    public void Compose_SlowRateCompositionPastRuntime_NeverPersistsTheLargerRawOffset()
    {
        // base 25:00, raw 10:00 of stream at 0.75x = 7:30 content -> 32:30 > runtime 30.
        long raw = MinutesToTicks(10);
        long composed = CreateReporter().ComposeItemAbsolutePosition(
            raw, MinutesToMs(25), MinutesToTicks(30), "RateCompose", ratePerMille: 750);

        Assert.Equal(MinutesToTicks(7.5), composed);
        Assert.True(composed < raw, "the conservative value must never exceed the guarded composition's scaled term");
    }

    /// <summary>
    /// ComposeEventPositionTicks reads the rate from the SAME launch scope as the
    /// base: seeding the queue manager's scope (base + rate) makes the entry-point
    /// compose the fully converted position.
    /// </summary>
    [Fact]
    public void ComposeEvent_ScopeRate_ScalesTheDeviceOffset()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id))
            .Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Id = id, RunTimeTicks = MinutesToTicks(60) });
        _queueManager.RecordLaunchBase(DeviceId, id.ToString(), MinutesToMs(20), enqueued: false, ratePerMille: 1500);

        long composed = CreateReporter().ComposeEventPositionTicks(
            DeviceId, id, MinutesToMs(5), "RateCompose", _queueManager, _fx.LibraryManager.Object);

        Assert.Equal(MinutesToTicks(27.5), composed);
    }
}
