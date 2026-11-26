using System;
using System.Net.Http;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.EntryPoints;
using Jellyfin.Plugin.AlexaSkill.Tests.Catalog;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// JF-967 catalog-recreate endpoint tests (the controller half; the engine
/// half lives in CatalogRecoveryTests): userId guards in the family's error
/// style, the recovery actually running for the resolved user, the
/// ITaskManager queueing of CatalogSyncTask (the verified trigger decision),
/// and the honest degradation when the queue call fails.
/// </summary>
[Collection("Plugin")]
public class ConfigurationControllerRecreateTests : PluginTestBase, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ITaskManager> _taskManagerMock = new();
    private readonly ConfigurationController _controller;

    public ConfigurationControllerRecreateTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-catalog-recreate-test");

        Plugin.Instance!.Configuration.Users.Clear();
        Plugin.Instance!.Configuration.LocaleModelStatuses.Clear();

        _controller = new ConfigurationController(
            Mock.Of<IUserManager>(),
            Mock.Of<ISessionManager>(),
            Mock.Of<ILibraryManager>(),
            _loggerFactory,
            new ModelDeploymentManager(
                Mock.Of<IHttpClientFactory>(),
                _loggerFactory.CreateLogger<ModelDeploymentManager>()),
            Mock.Of<IInteractionModelRedeployer>(),
            new CatalogLivenessChecker(_loggerFactory.CreateLogger<CatalogLivenessChecker>()),
            _taskManagerMock.Object);

        // The registration pre-check (JF-967 code review round 2) reads the
        // registered-task enumerable BEFORE queueing, through the worker's
        // inner ScheduledTask (the same resolution Jellyfin's own
        // QueueScheduledTask<T> performs): an unconfigured mock answers null,
        // which the controller reads as not-registered and never queues. The
        // default shape wraps a REAL CatalogSyncTask instance (cheap ctor
        // mocks); per-test overrides replace it.
        _taskManagerMock
            .Setup(m => m.ScheduledTasks)
            .Returns(new[]
            {
                Mock.Of<MediaBrowser.Model.Tasks.IScheduledTaskWorker>(w =>
                    w.ScheduledTask == new CatalogSyncTask(
                        _loggerFactory.CreateLogger<CatalogSyncTask>(),
                        null!,
                        null!,
                        null!))
            });
    }

    public void Dispose()
    {
        Plugin.Instance!.Configuration.Users.Clear();
        Plugin.Instance!.Configuration.LocaleModelStatuses.Clear();
        _loggerFactory.Dispose();
    }

    private static Entities.User AddUserWithCatalogIds()
    {
        // The construction policy (six fields + the -1h timestamp) lives in
        // the shared liveness-suite factory; only this suite's id VALUES are
        // local (JF-967 simplify round).
        Entities.User user = CatalogLivenessCheckerTests.CreateUser(
            artist: "amzn1.ask.catalog.recreate-artist",
            album: "amzn1.ask.catalog.recreate-album",
            series: "amzn1.ask.catalog.recreate-series",
            audiobook: "amzn1.ask.catalog.recreate-audiobook",
            movie: "amzn1.ask.catalog.recreate-movie",
            song: "amzn1.ask.catalog.recreate-song");
        Plugin.Instance!.Configuration.AddUser(user);
        return user;
    }

    private static string Body(Guid userId) =>
        JsonConvert.SerializeObject(new { userId = userId.ToString() });

    /// <summary>Round-trips a JsonResult's payload into an anonymous shape:
    /// the family's deserialize incantation as ONE copy (JF-967 simplify
    /// round).</summary>
    private static T Payload<T>(JsonResult json, T shape)
        => JsonConvert.DeserializeAnonymousType(JsonConvert.SerializeObject(json.Value), shape)!;

    /// <summary>A success JsonResult carries no explicit StatusCode (MVC
    /// renders it as HTTP 200); the error paths set theirs explicitly.</summary>
    private static void AssertSuccess(JsonResult json)
        => Assert.True(json.StatusCode is null or 200, $"expected a success status, got {json.StatusCode}");

    /// <summary>THE HAPPY PATH: the resolved user's six ids are nulled, the
    /// task manager queue receives CatalogSyncTask exactly once, one persist
    /// carries everything, and the response reports the old ids.</summary>
    [Fact]
    public void RecreateCatalogs_HappyPath_NullsIds_QueuesSync_RespondsWithOutcome()
    {
        Entities.User user = AddUserWithCatalogIds();
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            Caveat = CatalogLedgerCaveats.CatalogIdsDead,
            Error = "catalog liveness: 404 on stored catalog id(s): x",
            Source = CatalogLivenessChecker.CatalogLivenessLedgerSource
        });
        int savesBefore = TestHelpers.ConfigSerializerMock!.Invocations.Count;

        ActionResult result = _controller.RecreateCatalogs(Body(user.Id));

        var json = Assert.IsType<JsonResult>(result);
        AssertSuccess(json);
        Assert.Null(user.ArtistCatalogId);
        Assert.Null(user.SongCatalogId);
        Assert.Null(user.LastCatalogSync);
        _taskManagerMock.Verify(m => m.QueueScheduledTask<CatalogSyncTask>(), Times.Once);
        Assert.Equal(
            savesBefore + 1,
            TestHelpers.ConfigSerializerMock.Invocations.Count);

        var payload = Payload(json, new
            {
                success = false,
                nulledCatalogIds = new string[0],
                lastCatalogSyncCleared = false,
                ledgerRowsClearedOfDeadCaveat = 0,
                nothingCleared = true,
                syncQueued = false,
                userSyncable = false,
                otherUsersWithStoredIds = 0,
                message = string.Empty
            });
        Assert.True(payload!.success);
        Assert.False(payload.nothingCleared);
        Assert.Equal(6, payload.nulledCatalogIds!.Length);
        Assert.True(payload.lastCatalogSyncCleared);
        Assert.Equal(1, payload.ledgerRowsClearedOfDeadCaveat);
        Assert.True(payload.syncQueued);
        Assert.True(payload.userSyncable);
        Assert.Equal(0, payload.otherUsersWithStoredIds);
        Assert.Contains("catalog id(s)", payload.message, StringComparison.Ordinal);
    }

    /// <summary>The honesty guard for a user the sync task will SKIP: the
    /// resolved user carries stale ids but no SMAPI device token (the
    /// post-config-wipe shape), so the queued sync can never recreate their
    /// catalogs. The endpoint still succeeds (the clear is valid cleanup) but
    /// the response must say the pass will skip them instead of promising a
    /// recreate (JF-967 code review finding).</summary>
    [Fact]
    public void RecreateCatalogs_UserWithoutSmapiCredentials_ReportsTheSkip()
    {
        Entities.User user = TestHelpers.CreateSyncUser();
        user.SmapiDeviceToken = null;
        user.ArtistCatalogId = "amzn1.ask.catalog.recreate-stale";
        Plugin.Instance!.Configuration.AddUser(user);

        ActionResult result = _controller.RecreateCatalogs(Body(user.Id));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Payload(json, new { success = false, userSyncable = true, syncQueued = false, message = string.Empty });
        Assert.True(payload!.success);
        Assert.False(payload.userSyncable);
        Assert.True(payload.syncQueued);
        Assert.Null(user.ArtistCatalogId);
        Assert.Contains("SKIP this user", payload.message, StringComparison.Ordinal);
        Assert.Contains("Alexa authorization", payload.message, StringComparison.Ordinal);
    }

    /// <summary>The multi-user honesty guard: the caveat strip is global but
    /// the recovery heals ONE user, so other configured users carrying stored
    /// ids are counted and named in the response, and their ids stay
    /// untouched (the per-user contract).</summary>
    [Fact]
    public void RecreateCatalogs_OtherUsersWithStoredIds_ReportedAndUntouched()
    {
        Entities.User first = AddUserWithCatalogIds();
        Entities.User other = TestHelpers.CreateSyncUser();
        other.ArtistCatalogId = "amzn1.ask.catalog.recreate-other-artist";
        Plugin.Instance!.Configuration.AddUser(other);

        ActionResult result = _controller.RecreateCatalogs(Body(first.Id));

        var json = Assert.IsType<JsonResult>(result);
        var payload = Payload(json, new { otherUsersWithStoredIds = 0, message = string.Empty });
        Assert.Equal(1, payload!.otherUsersWithStoredIds);
        Assert.Contains("other configured user", payload.message, StringComparison.Ordinal);
        Assert.Equal("amzn1.ask.catalog.recreate-other-artist", other.ArtistCatalogId);
    }

    /// <summary>The registration pre-check arm (code review round 2): when
    /// CatalogSyncTask is NOT registered (mid-startup, registration failure),
    /// Jellyfin's queue call would log-and-return without throwing, so the
    /// endpoint must detect it via the registered-task enumerable and report
    /// the fallback instead of a false syncQueued=true.</summary>
    [Fact]
    public void RecreateCatalogs_TaskNotRegistered_ReportsFallback_NeverQueues()
    {
        Entities.User user = AddUserWithCatalogIds();
        _taskManagerMock
            .Setup(m => m.ScheduledTasks)
            .Returns(Array.Empty<MediaBrowser.Model.Tasks.IScheduledTaskWorker>());

        ActionResult result = _controller.RecreateCatalogs(Body(user.Id));

        var json = Assert.IsType<JsonResult>(result);
        AssertSuccess(json);
        var payload = Payload(json, new { syncQueued = true, message = string.Empty });
        Assert.False(payload!.syncQueued);
        Assert.Contains("could NOT be queued", payload.message, StringComparison.Ordinal);
        _taskManagerMock.Verify(m => m.QueueScheduledTask<CatalogSyncTask>(), Times.Never);
        Assert.Null(user.ArtistCatalogId);
    }

    /// <summary>The engine's mapped persist failure (code review round 2): a
    /// locked-save exception surfaces as a JSON 500 carrying the engine's
    /// guidance (in-memory mutated, disk possibly stale), not a raw error.
    /// Uses the JF-724 PersistInterceptorForTest seam.</summary>
    [Fact]
    public void RecreateCatalogs_PersistFailure_ReturnsJson500WithGuidance()
    {
        Entities.User user = AddUserWithCatalogIds();
        Plugin.Instance!.Configuration.PersistInterceptorForTest = () =>
            throw new System.IO.IOException("disk full (simulated)");
        try
        {
            ActionResult result = _controller.RecreateCatalogs(Body(user.Id));

            var json = Assert.IsType<JsonResult>(result);
            Assert.Equal(500, json.StatusCode);
            var payload = Payload(json, new { error = string.Empty });
            Assert.Contains("could not persist", payload!.error, StringComparison.Ordinal);
            _taskManagerMock.Verify(m => m.QueueScheduledTask<CatalogSyncTask>(), Times.Never);
        }
        finally
        {
            Plugin.Instance!.Configuration.PersistInterceptorForTest = null;
        }
    }

    /// <summary>Missing userId is the family's 400 error JSON.</summary>
    [Fact]
    public void RecreateCatalogs_MissingUserId_Returns400()
    {
        ActionResult result = _controller.RecreateCatalogs(JsonConvert.SerializeObject(new { }));

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, json.StatusCode);
        _taskManagerMock.Verify(m => m.QueueScheduledTask<CatalogSyncTask>(), Times.Never);
    }

    /// <summary>A malformed (non-GUID) userId is the resolver's 400.</summary>
    [Fact]
    public void RecreateCatalogs_MalformedUserId_Returns400()
    {
        ActionResult result = _controller.RecreateCatalogs(JsonConvert.SerializeObject(new { userId = "not-a-guid" }));

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(400, json.StatusCode);
    }

    /// <summary>An unknown (well-formed but absent) userId is the resolver's
    /// 404 error JSON (the user-skills endpoints' style), with nothing
    /// queued.</summary>
    [Fact]
    public void RecreateCatalogs_UnknownUser_Returns404_NothingQueued()
    {
        ActionResult result = _controller.RecreateCatalogs(Body(Guid.NewGuid()));

        var json = Assert.IsType<JsonResult>(result);
        Assert.Equal(404, json.StatusCode);
        var payload = Payload(json, new { error = string.Empty });
        Assert.Contains("Could not find user", payload!.error, StringComparison.Ordinal);
        _taskManagerMock.Verify(m => m.QueueScheduledTask<CatalogSyncTask>(), Times.Never);
    }

    /// <summary>The idempotent no-op: a user with nothing to clear still gets
    /// a 200 with nothingCleared true, the sync is STILL queued (the click's
    /// purpose is a recreate pass), and no persist happens.</summary>
    [Fact]
    public void RecreateCatalogs_NothingToClear_StillSucceedsAndQueues()
    {
        Entities.User user = TestHelpers.CreateSyncUser();
        Plugin.Instance!.Configuration.AddUser(user);
        int savesBefore = TestHelpers.ConfigSerializerMock!.Invocations.Count;

        ActionResult result = _controller.RecreateCatalogs(Body(user.Id));

        var json = Assert.IsType<JsonResult>(result);
        AssertSuccess(json);
        var payload = Payload(json, new { nothingCleared = false, syncQueued = false, message = string.Empty });
        Assert.True(payload!.nothingCleared);
        Assert.True(payload.syncQueued);
        Assert.Contains("Nothing to clear", payload.message, StringComparison.Ordinal);
        _taskManagerMock.Verify(m => m.QueueScheduledTask<CatalogSyncTask>(), Times.Once);
        Assert.Equal(
            savesBefore,
            TestHelpers.ConfigSerializerMock.Invocations.Count);
    }

    /// <summary>The degradation arm: a queue failure (task not registered,
    /// e.g. mid-startup) does NOT fail the recovery. The mutations and the
    /// single persist stand, the response reports syncQueued false, and the
    /// message names the next-scheduled-pass fallback.</summary>
    [Fact]
    public void RecreateCatalogs_QueueFailure_StillSucceeds_ReportsFallback()
    {
        Entities.User user = AddUserWithCatalogIds();
        _taskManagerMock
            .Setup(m => m.QueueScheduledTask<CatalogSyncTask>())
            .Throws(new InvalidOperationException("no registered worker"));

        ActionResult result = _controller.RecreateCatalogs(Body(user.Id));

        var json = Assert.IsType<JsonResult>(result);
        AssertSuccess(json);
        Assert.Null(user.ArtistCatalogId);
        Assert.Null(user.LastCatalogSync);
        var payload = Payload(json, new { success = false, syncQueued = true, message = string.Empty });
        Assert.True(payload!.success);
        Assert.False(payload.syncQueued);
        Assert.Contains("could NOT be queued", payload.message, StringComparison.Ordinal);
        Assert.Contains("next scheduled pass", payload.message, StringComparison.Ordinal);
    }
}
