#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-966 catalog liveness check tests. The probes run through the REAL Refit
/// client (SmapiManagement's handler-seam ctor, under the shared JF-545
/// auth-recovery wrapper) against a fake SMAPI backend, so the
/// 404-vs-unknown discrimination is exercised at the wire (a clean 404 arrives
/// as Refit's ApiException; a 500 or a network error must never read as dead).
/// The ledger tests pin the CatalogIdsDead caveat writer's wholesale-replace
/// shape, its clear arm, its survival through the startup capture's preserve,
/// the family-membership rule it joins in the sync writer's own-shape
/// predicate, and the auto-heal flag both ways.
/// </summary>
[Collection("Plugin")]
public class CatalogLivenessCheckerTests : PluginTestBase, IDisposable
{
    private const string ArtistId = "amzn1.ask.catalog.liveness-artist";
    private const string AlbumId = "amzn1.ask.catalog.liveness-album";
    private const string SeriesId = "amzn1.ask.catalog.liveness-series";
    private const string AudiobookId = "amzn1.ask.catalog.liveness-audiobook";
    private const string MovieId = "amzn1.ask.catalog.liveness-movie";
    private const string SongId = "amzn1.ask.catalog.liveness-song";

    private readonly List<(LogLevel Level, string Message)> _logRecords = new();
    private readonly ILoggerFactory _loggerFactory;

    public CatalogLivenessCheckerTests()
    {
        _loggerFactory = TestCaptureLogger.CreateCaptureLoggerFactory(_logRecords);
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-liveness-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private CatalogLivenessChecker CreateChecker() =>
        new(_loggerFactory.CreateLogger<CatalogLivenessChecker>());

    /// <summary>Internal: the recovery suite (JF-967) shares this user
    /// factory, passing its own id values; the body (the six-field
    /// assignment block plus the -1h LastCatalogSync policy) must not grow a
    /// second copy.</summary>
    internal static Entities.User CreateUser(
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

    /// <summary>Attaches the fake-backend SmapiManagement (the handler-seam
    /// test ctor) to the user's test override. Internal: the task-side
    /// liveness suite shares it.</summary>
    internal static void AttachSmapi(Entities.User user, FakeSmaCatalogHandler handler)
    {
        user.SetSmapiManagementForTest(new SmapiManagement(
            user.SmapiDeviceToken!,
            Microsoft.Extensions.Logging.LoggerFactory.Create(b => { }),
            handler));
    }

    /// <summary>Internal: the recovery suite (JF-967) shares this ledger
    /// seeder; this signature is the richer one (status and source are
    /// parameters), so callers wanting the recovery defaults pass them
    /// explicitly.</summary>
    internal static void SeedLedger(string locale, string status, CatalogLedgerCaveats caveat = CatalogLedgerCaveats.None, string? error = null, string? source = null)
    {
        Plugin.Instance!.Configuration.SetLocaleModelStatus(locale, new LocaleModelStatus
        {
            Status = status,
            LastUpdated = DateTime.UtcNow.AddHours(-2),
            Error = error,
            Caveat = caveat,
            Source = source ?? LibrarySyncService.CatalogSyncLedgerSource
        });
    }

    // -------------------------------------------------------------------------
    // Verdict discrimination (real Refit client against the fake backend)
    // -------------------------------------------------------------------------

    /// <summary>THE DETECTION (JF-966): a clean 404 through the real Refit
    /// client marks the catalog dead: the loud Error log names the dead id,
    /// every existing ledger row carries the CatalogIdsDead caveat with the
    /// id in Error, and a settled SUCCEEDED row keeps its status (the model
    /// build is still live; the catalog under it is what died).</summary>
    [Fact]
    public async Task Dead404_LoudErrorAndCaveatOnEveryRow_SucceededStatusPreserved()
    {
        SeedLedger("it-IT", "SUCCEEDED");
        SeedLedger("en-US", "SUCCEEDED");
        var handler = new FakeSmaCatalogHandler(new[] { (ArtistId, HttpStatusCode.NotFound) });
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        var records = TestCaptureLogger.Snapshot(_logRecords);
        Assert.Contains(records, r => r.Level == LogLevel.Error
            && r.Message.Contains("JF-966", StringComparison.Ordinal)
            && r.Message.Contains(ArtistId, StringComparison.Ordinal)
            && r.Message.Contains("INFRASTRUCTURE signature", StringComparison.Ordinal));

        var snapshot = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot();
        Assert.Equal(2, snapshot.Count);
        foreach (var entry in snapshot)
        {
            Assert.True(entry.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead), $"{entry.Locale} must carry the caveat");
            Assert.Equal("SUCCEEDED", entry.Status);
            Assert.Contains(ArtistId, entry.Error, StringComparison.Ordinal);
            Assert.Equal(CatalogLivenessChecker.CatalogLivenessLedgerSource, entry.Source);
        }

        // The row's CaveatText must name the dead state by name (the config
        // page renders this string).
        var status = snapshot.Single(e => e.Locale == "it-IT").ToStatus();
        Assert.Contains("DEAD on Amazon (404)", status.CaveatText, StringComparison.Ordinal);

        // Six probes fired (one per stored id), all GETs, on the liveness route.
        Assert.Equal(6, handler.GetCount);
        Assert.All(handler.GetUrls, url => Assert.Contains("/interactionModel/catalogs/", url));
    }

    /// <summary>Mixed: one dead id among alive ones. The caveat still fires
    /// (the family is compromised), and Error names exactly the dead ids.</summary>
    [Fact]
    public async Task MixedDeadAndAlive_CaveatCarriesOnlyTheDeadId()
    {
        SeedLedger("it-IT", "SUCCEEDED");
        var handler = new FakeSmaCatalogHandler(new[]
        {
            (ArtistId, HttpStatusCode.NotFound),
            (AlbumId, HttpStatusCode.OK),
            (SeriesId, HttpStatusCode.OK),
            (AudiobookId, HttpStatusCode.OK),
            (MovieId, HttpStatusCode.OK),
            (SongId, HttpStatusCode.OK)
        });
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.True(entry.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead));
        Assert.Contains(ArtistId, entry.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(AlbumId, entry.Error, StringComparison.Ordinal);
    }

    /// <summary>THE NOT-DEAD RULE: a 500 on every probe is UNKNOWN. The
    /// warning log fires, the ledger stays byte-identical, and no Error-level
    /// log may appear (only a clean 404 is allowed to be loud).</summary>
    [Fact]
    public async Task ServerError500_IsUnknown_LedgerUntouched_NoErrorLog()
    {
        SeedLedger("it-IT", "SUCCEEDED", error: "prior canary text");
        var handler = new FakeSmaCatalogHandler(defaultStatus: HttpStatusCode.InternalServerError);
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        var records = TestCaptureLogger.Snapshot(_logRecords);
        Assert.DoesNotContain(records, r => r.Level == LogLevel.Error);
        Assert.Contains(records, r => r.Level == LogLevel.Warning
            && r.Message.Contains("UNKNOWN", StringComparison.Ordinal)
            && r.Message.Contains("JF-966", StringComparison.Ordinal));

        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal(CatalogLedgerCaveats.None, entry.Caveat);
        Assert.Equal("SUCCEEDED", entry.Status);
        Assert.Equal("prior canary text", entry.Error);
        Assert.Equal(6, handler.GetCount);
    }

    /// <summary>A network-level failure (the handler throws instead of
    /// answering) is the same UNKNOWN: not dead, ledger untouched.</summary>
    [Fact]
    public async Task NetworkError_IsUnknown_LedgerUntouched()
    {
        SeedLedger("it-IT", "SUCCEEDED");
        var handler = new FakeSmaCatalogHandler(networkError: true);
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        var records = TestCaptureLogger.Snapshot(_logRecords);
        Assert.DoesNotContain(records, r => r.Level == LogLevel.Error);
        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal(CatalogLedgerCaveats.None, entry.Caveat);
    }

    /// <summary>A user with NO stored ids costs ZERO SMAPI calls: a null
    /// catalog was never created and cannot be dead.</summary>
    [Fact]
    public async Task NoStoredIds_ZeroSmaCalls_NoWrites()
    {
        var handler = new FakeSmaCatalogHandler();
        var user = CreateUser(artist: null, album: null, series: null, audiobook: null, movie: null, song: null);
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        Assert.Equal(0, handler.GetCount);
        Assert.Empty(TestCaptureLogger.Snapshot(_logRecords).Where(r => r.Level >= LogLevel.Warning));
        Assert.Empty(Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot());
    }

    // -------------------------------------------------------------------------
    // Ledger shapes: the dead-write row and the clear arm
    // -------------------------------------------------------------------------

    /// <summary>An unsettled previous status clamps to the healthy-neutral
    /// "Skipped" on the dead write (the PreservedOrSkippedStatus rule: a
    /// momentary IN_PROGRESS must not freeze into a permanent failing row).</summary>
    [Fact]
    public async Task DeadWrite_ClampsUnsettledStatusToSkipped()
    {
        SeedLedger("it-IT", "IN_PROGRESS", source: "Embedded");
        var handler = new FakeSmaCatalogHandler(new[] { (ArtistId, HttpStatusCode.NotFound) });
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.Equal("Skipped", entry.Status);
        Assert.True(entry.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead));
    }

    /// <summary>The carry doctrine (the no-PUT writer's rule, JF-709 rework
    /// F1): a previous row with a FOREIGN diagnostic (no own-shape caveat
    /// bits) has that diagnostic carried ATTRIBUTED beside the dead-ids text,
    /// never silently dropped by the wholesale replace. The checker's own
    /// dead rows (own-shape) replace wholesale instead.</summary>
    [Fact]
    public async Task DeadWrite_CarriesAForeignDiagnosticAttributed()
    {
        SeedLedger("it-IT", "SUCCEEDED", error: "canary mismatch text");
        var handler = new FakeSmaCatalogHandler(new[] { (ArtistId, HttpStatusCode.NotFound) });
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        var entry = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT");
        Assert.True(entry.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead));
        Assert.Contains(ArtistId, entry.Error, StringComparison.Ordinal);
        Assert.Contains("previous run: canary mismatch text", entry.Error, StringComparison.Ordinal);
    }

    /// <summary>The clear arm: an all-alive pass strips CatalogIdsDead from
    /// the rows that carry it (Error included, the row was wholly the check's)
    /// and touches rows that do not carry it (byte-identical, compose
    /// declined).</summary>
    [Fact]
    public async Task AllAlive_ClearsTheBitFromItsOwnRowsAndLeavesOthersVerbatim()
    {
        SeedLedger("it-IT", "SUCCEEDED", caveat: CatalogLedgerCaveats.CatalogIdsDead, error: "catalog liveness: 404 on stored catalog id(s): x", source: CatalogLivenessChecker.CatalogLivenessLedgerSource);
        SeedLedger("en-US", "SUCCEEDED");
        var before = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "en-US");

        var handler = new FakeSmaCatalogHandler();
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        var snapshot = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot();
        var cleared = snapshot.Single(e => e.Locale == "it-IT");
        Assert.Equal(CatalogLedgerCaveats.None, cleared.Caveat);
        Assert.Null(cleared.Error);
        Assert.Equal("SUCCEEDED", cleared.Status);
        Assert.Equal(CatalogLivenessChecker.CatalogLivenessLedgerSource, cleared.Source);

        // The foreign row was declined (compose returned null): nothing about
        // it changed, LastUpdated included.
        var untouched = snapshot.Single(e => e.Locale == "en-US");
        Assert.Equal(before.LastUpdated, untouched.LastUpdated);
        Assert.Equal(before.Error, untouched.Error);
        Assert.Equal(before.Caveat, untouched.Caveat);
    }

    /// <summary>The dead caveat is DURABLE: the startup capture's preserve
    /// carries it (the preserve's recognition accepts any row carrying caveat
    /// bits, and its mask drops only the run-scoped NoCatalogPut bit), so a
    /// restart with the skip gate closed cannot silence the signal.</summary>
    [Fact]
    public async Task DeadCaveat_SurvivesTheCleanCapturePreserve()
    {
        SeedLedger("it-IT", "SUCCEEDED", caveat: CatalogLedgerCaveats.CatalogIdsDead, error: "catalog liveness: 404 on stored catalog id(s): x", source: CatalogLivenessChecker.CatalogLivenessLedgerSource);
        var existing = Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT").ToStatus();

        var preserved = LibrarySyncService.PreserveLedgerCaveatAcrossCapture(existing);

        Assert.NotNull(preserved);
        Assert.True(preserved!.Value.Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead));
        Assert.Contains("404", preserved.Value.Error, StringComparison.Ordinal);
    }

    /// <summary>The dead caveat is the sync writers' OWN shape (JF-966's
    /// membership rule in IsOwnShapeLedgerCaveat): a frozen leg rewriting a
    /// liveness row must REPLACE its Error wholesale, never carry it as a
    /// "previous run:" foreign diagnostic that outlives the state it
    /// described. Pinned through the predicate itself; the boundary bits
    /// document what stays foreign.</summary>
    [Fact]
    public void DeadCaveat_IsOwnShapeForTheSyncLedgerWriters()
    {
        bool Invoke(CatalogLedgerCaveats caveat)
        {
            var method = typeof(LibrarySyncService).GetMethod(
                "IsOwnShapeLedgerCaveat",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(method != null, "IsOwnShapeLedgerCaveat must still exist (this pin's lookup target)");
            return (bool)method!.Invoke(null, new object[] { caveat })!;
        }

        Assert.True(Invoke(CatalogLedgerCaveats.CatalogIdsDead));
        Assert.True(Invoke(CatalogLedgerCaveats.FrozenCatalogs));
        Assert.True(Invoke(CatalogLedgerCaveats.NoCatalogPut));
        Assert.False(Invoke(CatalogLedgerCaveats.ObservedBuildErrors));
    }

    /// <summary>The completeness pin for the per-type field map (the JF-711/
    /// JF-727 doctrine's lighter half): StoredCatalogs must cover EVERY User
    /// property ending in CatalogId, so an eighth catalog type's one-row edit
    /// in the sync table without a liveness row fails HERE instead of leaving
    /// the new type silently unprobed.</summary>
    [Fact]
    public void StoredCatalogs_CoversEveryStoredCatalogIdProperty()
    {
        var user = CreateUser();
        var method = typeof(CatalogLivenessChecker).GetMethod(
            "StoredCatalogs",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(method != null, "StoredCatalogs must still exist (this pin's lookup target)");
        var stored = Assert.IsType<List<CatalogLivenessChecker.StoredCatalog>>(
            method!.Invoke(null, new object[] { user }));

        var expectedTypes = typeof(Entities.User)
            .GetProperties()
            .Where(p => p.Name.EndsWith("CatalogId", StringComparison.Ordinal))
            .Select(p => p.Name.Replace("CatalogId", string.Empty, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var coveredTypes = stored.Select(s => s.Type.ToString()).ToHashSet(StringComparer.Ordinal);

        Assert.True(
            expectedTypes.SetEquals(coveredTypes),
            $"StoredCatalogs must probe every stored catalog id property; missing: [{string.Join(", ", expectedTypes.Except(coveredTypes))}], unknown: [{string.Join(", ", coveredTypes.Except(expectedTypes))}]");
    }

    // -------------------------------------------------------------------------
    // The auto-heal flag
    // -------------------------------------------------------------------------

    /// <summary>Auto-heal ON: exactly the dead ids are nulled, the survivors
    /// stay, LastCatalogSync is cleared (the documented force-next-sync
    /// action), and the change is persisted through the locked save.</summary>
    [Fact]
    public async Task AutoHealOn_DeadIdsNulled_LastCatalogSyncCleared_Persisted()
    {
        Plugin.Instance!.Configuration.CatalogAutoHealDeadIds = true;
        SeedLedger("it-IT", "SUCCEEDED");
        var handler = new FakeSmaCatalogHandler(new[] { (ArtistId, HttpStatusCode.NotFound), (MovieId, HttpStatusCode.NotFound) });
        var user = CreateUser();
        AttachSmapi(user, handler);
        int savesBefore = TestHelpers.ConfigSerializerMock!.Invocations.Count;

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        Assert.Null(user.ArtistCatalogId);
        Assert.Null(user.MovieCatalogId);
        Assert.Equal(AlbumId, user.AlbumCatalogId);
        Assert.Equal(SeriesId, user.SeriesCatalogId);
        Assert.Equal(AudiobookId, user.AudiobookCatalogId);
        Assert.Equal(SongId, user.SongCatalogId);
        Assert.Null(user.LastCatalogSync);
        Assert.True(
            TestHelpers.ConfigSerializerMock.Invocations.Count > savesBefore,
            "the auto-heal must persist the nulled ids through the locked save path");

        // Healing does not mute the signal: the loud error and the caveat both
        // still fire (the state is broken until the next scheduled sync).
        Assert.Contains(TestCaptureLogger.Snapshot(_logRecords), r => r.Level == LogLevel.Error && r.Message.Contains("JF-966", StringComparison.Ordinal));
        Assert.True(Plugin.Instance!.Configuration.GetLocaleModelStatusSnapshot().Single(e => e.Locale == "it-IT").Caveat.HasFlag(CatalogLedgerCaveats.CatalogIdsDead));
    }

    /// <summary>Auto-heal OFF (the default): dead ids are reported loudly but
    /// every stored id survives untouched.</summary>
    [Fact]
    public async Task AutoHealOff_Default_IdsUntouched()
    {
        Assert.False(Plugin.Instance!.Configuration.CatalogAutoHealDeadIds, "the flag must default OFF");
        SeedLedger("it-IT", "SUCCEEDED");
        var handler = new FakeSmaCatalogHandler(new[] { (ArtistId, HttpStatusCode.NotFound) });
        var user = CreateUser();
        AttachSmapi(user, handler);

        await CreateChecker().CheckUserAsync(user, CancellationToken.None);

        Assert.Equal(ArtistId, user.ArtistCatalogId);
        Assert.Equal(AlbumId, user.AlbumCatalogId);
        Assert.Equal(SeriesId, user.SeriesCatalogId);
        Assert.Equal(AudiobookId, user.AudiobookCatalogId);
        Assert.Equal(MovieId, user.MovieCatalogId);
        Assert.Equal(SongId, user.SongCatalogId);
        Assert.NotNull(user.LastCatalogSync);
    }

    /// <summary>The fake SMAPI catalog backend: routes get-catalog by catalog
    /// id, records every GET, and can answer 200/404/500 or throw a network
    /// error. Anything but a GET is unexpected and 404s.</summary>
    internal sealed class FakeSmaCatalogHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _defaultStatus;
        private readonly bool _networkError;
        private readonly Dictionary<string, HttpStatusCode> _statusById;

        /// <param name="routes">Per-catalog-id status overrides; null answers
        /// every id with <paramref name="defaultStatus"/>.</param>
        /// <param name="defaultStatus">Answer for ids with no explicit route
        /// (and for routes-less fakes): OK by default; InternalServerError
        /// makes every probe fail as UNKNOWN.</param>
        /// <param name="networkError">Throw from the handler instead of
        /// answering (the network-level UNKNOWN shape).</param>
        public FakeSmaCatalogHandler(
            (string CatalogId, HttpStatusCode Status)[]? routes = null,
            HttpStatusCode defaultStatus = HttpStatusCode.OK,
            bool networkError = false)
        {
            _defaultStatus = defaultStatus;
            _networkError = networkError;
            _statusById = routes?.ToDictionary(r => r.CatalogId, r => r.Status) ?? new Dictionary<string, HttpStatusCode>();
        }

        public List<string> GetUrls { get; } = new();

        public int GetCount => GetUrls.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"unexpected {request.Method}", Encoding.UTF8, "application/json")
                });
            }

            GetUrls.Add(request.RequestUri!.ToString());

            if (_networkError)
            {
                throw new HttpRequestException("liveness-fake network failure");
            }

            string path = request.RequestUri.AbsolutePath;
            HttpStatusCode status = _defaultStatus;
            foreach (var pair in _statusById)
            {
                if (path.EndsWith($"/interactionModel/catalogs/{pair.Key}", StringComparison.Ordinal))
                {
                    status = pair.Value;
                    break;
                }
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.OK
                        ? $"{{\"catalog\":{{\"name\":\"{path}\"}},\"totalVersions\":1}}"
                        : "{\"message\":\"fake\"}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
