#nullable enable

using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-967 catalog recovery tests (the engine half, CatalogLivenessChecker.
/// RecreateStoredCatalogs, driven directly; the endpoint half lives in
/// ConfigurationControllerRecreateTests): the unconditional six-field clear
/// (whitespace garbage included), the LastCatalogSync force-next-sync null,
/// the ledger audit row (old ids, recovery Source) with the dead-caveat
/// strip, the ONE locked persist for user mutations plus sweep together, and
/// the idempotent no-op that persists nothing.
/// </summary>
[Collection("Plugin")]
public class CatalogRecoveryTests : PluginTestBase, IDisposable
{
    private const string ArtistId = "amzn1.ask.catalog.recovery-artist";
    private const string AlbumId = "amzn1.ask.catalog.recovery-album";
    private const string SeriesId = "amzn1.ask.catalog.recovery-series";
    private const string AudiobookId = "amzn1.ask.catalog.recovery-audiobook";
    private const string MovieId = "amzn1.ask.catalog.recovery-movie";
    private const string SongId = "amzn1.ask.catalog.recovery-song";

    private readonly ILoggerFactory _loggerFactory;

    public CatalogRecoveryTests()
    {
        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-catalog-recovery-test");
        Plugin.Instance!.Configuration.LocaleModelStatuses.Clear();
    }

    public void Dispose()
    {
        Plugin.Instance!.Configuration.LocaleModelStatuses.Clear();
        _loggerFactory.Dispose();
    }

    private CatalogLivenessChecker CreateChecker() =>
        new(_loggerFactory.CreateLogger<CatalogLivenessChecker>());

    private static Entities.User CreateUser(
        string? artist = ArtistId,
        string? album = AlbumId,
        string? series = SeriesId,
        string? audiobook = AudiobookId,
        string? movie = MovieId,
        string? song = SongId)
    {
        Entities.User user = TestHelpers.CreateSyncUser();
        user.ArtistCatalogId = artist;
        user.AlbumCatalogId = album;
        user.SeriesCatalogId = series;
        user.AudiobookCatalogId = audiobook;
        user.MovieCatalogId = movie;
        user.SongCatalogId = song;
        user.LastCatalogSync = DateTime.UtcNow.AddHours(-1);
        return user;
    }

    private static void SeedLedger(string locale, CatalogLedgerCaveats caveat = CatalogLedgerCaveats.None, string? error = null, string source = LibrarySyncService.CatalogSyncLedgerSource)
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus(locale, new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            LastUpdated = DateTime.UtcNow.AddHours(-2),
            Error = error,
            Caveat = caveat,
            Source = source
        });
    }

    /// <summary>THE HAPPY PATH: all six ids are nulled, LastCatalogSync is
    /// cleared, every ledger row carries the audit (recovery Source, the OLD
    /// ids in Error, the dead caveat stripped, a settled SUCCEEDED status
    /// kept), and exactly ONE persist carries the user mutations and the
    /// sweep together.</summary>
    [Fact]
    public void HappyPath_SixIdsNulled_AuditRowWritten_CaveatStripped_PersistsOnce()
    {
        SeedLedger("it-IT", CatalogLedgerCaveats.CatalogIdsDead, error: "catalog liveness: 404 on stored catalog id(s): " + ArtistId, source: CatalogLivenessChecker.CatalogLivenessLedgerSource);
        SeedLedger("en-US");
        var user = CreateUser();
        int savesBefore = TestHelpers.ConfigSerializerMock!.Invocations.Count;

        CatalogLivenessChecker.CatalogRecoveryOutcome outcome = CreateChecker().RecreateStoredCatalogs(user);

        Assert.Null(user.ArtistCatalogId);
        Assert.Null(user.AlbumCatalogId);
        Assert.Null(user.SeriesCatalogId);
        Assert.Null(user.AudiobookCatalogId);
        Assert.Null(user.MovieCatalogId);
        Assert.Null(user.SongCatalogId);
        Assert.Null(user.LastCatalogSync);
        Assert.True(outcome.AnythingCleared);
        Assert.True(outcome.LastCatalogSyncCleared);
        Assert.Equal(6, outcome.NulledIds.Count);
        Assert.Contains($"Artist={ArtistId}", outcome.NulledIds, StringComparer.Ordinal);
        Assert.Contains($"Song={SongId}", outcome.NulledIds, StringComparer.Ordinal);

        // ONE persist: the user mutations and the ledger sweep ride a single
        // locked save (never partially persisted).
        Assert.Equal(
            savesBefore + 1,
            TestHelpers.ConfigSerializerMock.Invocations.Count);

        var snapshot = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot();
        Assert.Equal(2, snapshot.Count);
        foreach (var entry in snapshot)
        {
            Assert.Equal(CatalogLivenessChecker.CatalogRecoveryLedgerSource, entry.Source);
            Assert.Equal("SUCCEEDED", entry.Status);
            Assert.Equal(CatalogLedgerCaveats.None, entry.Caveat);
            Assert.Contains(ArtistId, entry.Error, StringComparison.Ordinal);
            Assert.Contains("LastCatalogSync", entry.Error, StringComparison.Ordinal);
            Assert.Contains("JF-967", entry.Error, StringComparison.Ordinal);
        }

        // The caveat row was counted; the clean row was not.
        Assert.Equal(1, outcome.CaveatRowsCleared);

        // The liveness dead text does NOT survive as a carried diagnostic
        // (own-shape rows replace wholesale, the DeadCompose rule).
        Assert.DoesNotContain("catalog liveness: 404", snapshot.Single(e => e.Locale == "it-IT").Error, StringComparison.Ordinal);
    }

    /// <summary>THE UNCONDITIONAL-CLEAR RULE: the probe map's null filter must
    /// NOT apply to the recovery. A whitespace-garbage stored id (which
    /// StoredCatalogs' probe pass would skip as unset) is nulled too, and the
    /// garbage value is still captured into the audit as an old id.</summary>
    [Fact]
    public void WhitespaceGarbageId_IsNulledToo_AndAudited()
    {
        var user = CreateUser(album: "   ");
        SeedLedger("it-IT");

        CatalogLivenessChecker.CatalogRecoveryOutcome outcome = CreateChecker().RecreateStoredCatalogs(user);

        Assert.Null(user.AlbumCatalogId);
        Assert.Contains("Album=   ", outcome.NulledIds, StringComparer.Ordinal);
        Assert.Contains("Album=   ", Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT").Error, StringComparison.Ordinal);
    }

    /// <summary>A foreign (non-own-shape) diagnostic on a swept row is carried
    /// ATTRIBUTED beside the audit text, exactly like the dead-write row's
    /// carry doctrine: the canary mismatch the recreate cannot heal stays
    /// visible until the next sync rewrites the row.</summary>
    [Fact]
    public void AuditRow_CarriesAForeignDiagnosticAttributed()
    {
        SeedLedger("it-IT", error: "canary mismatch text");
        var user = CreateUser();

        CreateChecker().RecreateStoredCatalogs(user);

        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Contains("JF-967", entry.Error, StringComparison.Ordinal);
        Assert.Contains("previous run: canary mismatch text", entry.Error, StringComparison.Ordinal);
    }

    /// <summary>Non-dead caveat bits SURVIVE the sweep with their payload (the
    /// bit-iff-payload rule): a frozen row keeps FrozenCatalogs and its
    /// names; only the CatalogIdsDead bit is stripped.</summary>
    [Fact]
    public void Sweep_KeepsForeignCaveatBitsWithTheirPayload()
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
            Caveat = CatalogLedgerCaveats.FrozenCatalogs | CatalogLedgerCaveats.CatalogIdsDead,
            FrozenCatalogTypes = "Artist,Album",
            Error = "catalog liveness: 404 on stored catalog id(s): x",
            Source = CatalogLivenessChecker.CatalogLivenessLedgerSource
        });
        var user = CreateUser();

        CatalogLivenessChecker.CatalogRecoveryOutcome outcome = CreateChecker().RecreateStoredCatalogs(user);

        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal(CatalogLedgerCaveats.FrozenCatalogs, entry.Caveat);
        Assert.Equal("Artist,Album", entry.FrozenCatalogTypes);
        Assert.Equal(1, outcome.CaveatRowsCleared);
    }

    /// <summary>THE IDEMPOTENT NO-OP: a user with no stored ids, no
    /// last-sync timestamp, and no caveat anywhere still succeeds, reports
    /// nothing-cleared, writes no ledger row, and persists NOTHING (a second
    /// click after a completed recovery costs zero saves).</summary>
    [Fact]
    public void NoStoredIdsNoCaveat_NoOp_PersistsNothing()
    {
        SeedLedger("it-IT");
        var user = CreateUser(artist: null, album: null, series: null, audiobook: null, movie: null, song: null);
        user.LastCatalogSync = null;
        int savesBefore = TestHelpers.ConfigSerializerMock!.Invocations.Count;

        CatalogLivenessChecker.CatalogRecoveryOutcome outcome = CreateChecker().RecreateStoredCatalogs(user);

        Assert.False(outcome.AnythingCleared);
        Assert.Empty(outcome.NulledIds);
        Assert.Equal(0, outcome.CaveatRowsCleared);
        Assert.False(outcome.LastCatalogSyncCleared);
        Assert.Equal(
            savesBefore,
            TestHelpers.ConfigSerializerMock.Invocations.Count);

        // The healthy ledger row was not swept either (the audit-stamp pass
        // runs only when something changed).
        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, entry.Source);
    }

    /// <summary>A user with ids but LastCatalogSync already null still
    /// audits and persists (the ids are the change), and the audit text omits
    /// the last-sync clause instead of lying about it.</summary>
    [Fact]
    public void LastSyncAlreadyNull_StillAudits_TextOmitsLastSyncClause()
    {
        SeedLedger("it-IT");
        var user = CreateUser();
        user.LastCatalogSync = null;

        CatalogLivenessChecker.CatalogRecoveryOutcome outcome = CreateChecker().RecreateStoredCatalogs(user);

        Assert.True(outcome.AnythingCleared);
        Assert.False(outcome.LastCatalogSyncCleared);
        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Contains("nulled stored catalog ids", entry.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("cleared LastCatalogSync", entry.Error, StringComparison.Ordinal);
    }

    /// <summary>A caveat-only recovery (auto-heal already nulled the ids but
    /// the ledger still carries the dead bit) sweeps, strips, and persists
    /// even though no id and no timestamp changed.</summary>
    [Fact]
    public void CaveatOnly_SweepsAndPersists()
    {
        SeedLedger("it-IT", CatalogLedgerCaveats.CatalogIdsDead, error: "catalog liveness: 404 on stored catalog id(s): x", source: CatalogLivenessChecker.CatalogLivenessLedgerSource);
        var user = CreateUser(artist: null, album: null, series: null, audiobook: null, movie: null, song: null);
        user.LastCatalogSync = null;
        int savesBefore = TestHelpers.ConfigSerializerMock!.Invocations.Count;

        CatalogLivenessChecker.CatalogRecoveryOutcome outcome = CreateChecker().RecreateStoredCatalogs(user);

        Assert.True(outcome.AnythingCleared);
        Assert.Equal(1, outcome.CaveatRowsCleared);
        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal(CatalogLedgerCaveats.None, entry.Caveat);
        Assert.Equal(
            savesBefore + 1,
            TestHelpers.ConfigSerializerMock.Invocations.Count);
    }
}
