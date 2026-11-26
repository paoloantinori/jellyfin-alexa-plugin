#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.EntryPoints;
using Jellyfin.Plugin.AlexaSkill.Tests.Catalog;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.EntryPoints;

/// <summary>
/// JF-966: the liveness check's piggyback on CatalogSyncTask's &lt;12h skip
/// path. A skipped user's stored catalog ids get one get-catalog probe each
/// (the skip path costs zero SMAPI calls otherwise): a clean 404 is the LOUD
/// error plus the durable CatalogIdsDead caveat, all-alive logs nothing and
/// writes nothing, and a user with no stored ids costs zero calls. The sync
/// itself must not run for a skipped user (the gate's contract is unchanged).
/// </summary>
[Collection("Plugin")]
public class CatalogSyncTaskLivenessTests : PluginTestBase, IDisposable
{
    private const string ArtistId = "amzn1.ask.catalog.task-artist";

    private readonly List<(LogLevel Level, string Message)> _logRecords = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly CatalogLivenessCheckerTests.FakeSmaCatalogHandler _handler = new(new[] { (ArtistId, HttpStatusCode.NotFound) });

    public CatalogSyncTaskLivenessTests()
    {
        _loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(_logRecords);
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-liveness-task-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    /// <summary>The task under test. The skip path touches neither the sync
    /// service nor the user manager, so the collaborators are null: the
    /// LibrarySyncService instance exists only to carry the library-manager
    /// mock for the sync-never-ran verification.</summary>
    private CatalogSyncTask CreateTask()
    {
        var syncService = new LibrarySyncService(
            _libraryManagerMock.Object,
            null!,
            null!,
            _loggerFactory.CreateLogger<LibrarySyncService>());

        return new CatalogSyncTask(
            _loggerFactory.CreateLogger<CatalogSyncTask>(),
            syncService,
            new Mock<IUserManager>().Object,
            new CatalogLivenessChecker(_loggerFactory.CreateLogger<CatalogLivenessChecker>()));
    }

    private Entities.User CreateUserIntoConfig(DateTimeOffset? lastSync, CatalogLivenessCheckerTests.FakeSmaCatalogHandler? handler = null)
    {
        Entities.User user = TestHelpers.CreateSyncUser();
        user.ArtistCatalogId = ArtistId;
        user.AlbumCatalogId = "amzn1.ask.catalog.task-album";
        user.SeriesCatalogId = "amzn1.ask.catalog.task-series";
        user.AudiobookCatalogId = "amzn1.ask.catalog.task-audiobook";
        user.MovieCatalogId = "amzn1.ask.catalog.task-movie";
        user.SongCatalogId = null;
        user.LastCatalogSync = lastSync?.UtcDateTime;
        CatalogLivenessCheckerTests.AttachSmapi(user, handler ?? _handler);
        Plugin.Instance!.Configuration.Users.Add(user);
        return user;
    }

    /// <summary>THE PIGGYBACK: a user inside the 12h skip gate with a dead
    /// catalog id still gets checked: the loud error fires, the pre-existing
    /// ledger rows carry the caveat, five GETs ride the skip (one per stored
    /// id; the song catalog is unset on this user), and the sync itself never
    /// runs (the library manager is never touched and LastCatalogSync does
    /// not advance).</summary>
    [Fact]
    public async Task SkipPath_DeadId_LoudErrorAndCaveat_SyncDoesNotRun()
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            LastUpdated = DateTime.UtcNow.AddHours(-2),
            Source = LibrarySyncService.CatalogSyncLedgerSource
        });
        var user = CreateUserIntoConfig(DateTimeOffset.UtcNow.AddHours(-1));
        var beforeSync = user.LastCatalogSync;

        await CreateTask().ExecuteAsync(new NoopProgress(), CancellationToken.None);

        var records = TestCaptureLogger.Snapshot(_logRecords);
        Assert.Contains(records, r => r.Level == LogLevel.Error
            && r.Message.Contains("JF-966", StringComparison.Ordinal)
            && r.Message.Contains(ArtistId, StringComparison.Ordinal));

        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.True(entry.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead));
        Assert.Contains(ArtistId, entry.Error, StringComparison.Ordinal);
        Assert.Equal("SUCCEEDED", entry.Status);

        Assert.Equal(5, _handler.GetCount);
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        Assert.Equal(beforeSync, user.LastCatalogSync);
    }

    /// <summary>All alive: the skip path stays quiet, with no error, no
    /// caveat, and no ledger write, at the accepted price of the reads.</summary>
    [Fact]
    public async Task SkipPath_AllAlive_Quiet_NoCaveat()
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            LastUpdated = DateTime.UtcNow.AddHours(-2),
            Source = LibrarySyncService.CatalogSyncLedgerSource
        });
        CreateUserIntoConfig(DateTimeOffset.UtcNow.AddHours(-1), new CatalogLivenessCheckerTests.FakeSmaCatalogHandler());

        await CreateTask().ExecuteAsync(new NoopProgress(), CancellationToken.None);

        var records = TestCaptureLogger.Snapshot(_logRecords);
        Assert.DoesNotContain(records, r => r.Level >= LogLevel.Warning);
        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal(CatalogLedgerCaveats.None, entry.Caveat);
    }

    /// <summary>A user with no stored catalog ids costs ZERO SMAPI calls on
    /// the skip path (nothing stored can be dead; the check returns before
    /// resolving the SMAPI client, so the attached handler must record
    /// nothing).</summary>
    [Fact]
    public async Task SkipPath_NoStoredIds_ZeroCalls()
    {
        var user = CreateUserIntoConfig(DateTimeOffset.UtcNow.AddHours(-1));
        user.ArtistCatalogId = null;
        user.AlbumCatalogId = null;
        user.SeriesCatalogId = null;
        user.AudiobookCatalogId = null;
        user.MovieCatalogId = null;
        user.SongCatalogId = null;

        await CreateTask().ExecuteAsync(new NoopProgress(), CancellationToken.None);

        Assert.Equal(0, _handler.GetCount);
        Assert.DoesNotContain(TestCaptureLogger.Snapshot(_logRecords), r => r.Level >= LogLevel.Warning);
    }

    /// <summary>No-op progress reporter for task execution.</summary>
    private sealed class NoopProgress : IProgress<double>
    {
        public void Report(double value)
        {
        }
    }
}
