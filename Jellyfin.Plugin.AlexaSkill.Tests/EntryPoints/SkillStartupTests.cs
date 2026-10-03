using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.EntryPoints;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.EntryPoints;

[Collection("Plugin")]
public class SkillStartupTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SearchResultCache _searchCache;
    private readonly JellyfinConnectivityChecker _connectivityChecker;

    public SkillStartupTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _loggerFactory = LoggerFactory.Create(b => { });
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton<SearchResultCache>();
        var provider = services.BuildServiceProvider();
        _httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
        _searchCache = provider.GetRequiredService<SearchResultCache>();
        _connectivityChecker = new JellyfinConnectivityChecker(
            _loggerFactory.CreateLogger<JellyfinConnectivityChecker>());

        // The capture writes through Plugin.Instance (Configuration + save).
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            c => { },
            "alexa-startup-capture-test");
    }

    private SkillStartup CreateStartup()
    {
        var mdm = new ModelDeploymentManager(_httpClientFactory, _loggerFactory.CreateLogger<ModelDeploymentManager>());
        var deviceQueueManager = TestHelpers.CreateDeviceQueueManager("alexa_test_queues");
        var positionTracker = new Jellyfin.Plugin.AlexaSkill.Alexa.Playback.AudiobookPositionTracker(
            TestHelpers.CreateRegisteredTempDir("alexa_test_pos"),
            _loggerFactory.CreateLogger<Jellyfin.Plugin.AlexaSkill.Alexa.Playback.AudiobookPositionTracker>());
        return new SkillStartup(_sessionManagerMock.Object, _loggerFactory, _httpClientFactory, mdm, _searchCache, new CircuitBreaker(), new RequestCounters(), _connectivityChecker, deviceQueueManager, positionTracker);
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var startup = CreateStartup();

        await startup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var startup = CreateStartup();

        startup.Dispose();
        startup.Dispose();
    }

    [Fact]
    public async Task StopAsync_CancelsBackgroundWork()
    {
        var startup = CreateStartup();

        // StopAsync should complete without error even without Plugin.Instance
        await startup.StopAsync(CancellationToken.None);

        startup.Dispose();
    }

    [Fact]
    public async Task StopAsync_WithCancelledToken_Completes()
    {
        var startup = CreateStartup();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await startup.StopAsync(cts.Token);

        startup.Dispose();
    }

    /// <summary>
    /// Builds the SMAPI skill-status snapshot the capture consumes: one locale
    /// entry with the given build state and optional build errors. The capture
    /// reads only the per-locale InteractionModel surface, so no Manifest is
    /// served; if that ever changes, this fixture fails loudly.
    /// </summary>
    private static SkillStatus StatusFor(string locale, SkillStatusState state, InvocationError[]? errors = null) => new()
    {
        InteractionModel = new Dictionary<string, StatusManifest>
        {
            [locale] = new StatusManifest
            {
                LastModified = new LastModifiedInformation { Status = state },
                Errors = errors,
            },
        },
    };

    /// <summary>
    /// Multi-locale snapshot with per-locale control, including the JF-722
    /// malformed shape: a null State produces a StatusManifest whose LastModified
    /// is null (the freshly-created-skill entry whose build has not started).
    /// Dictionary enumeration follows insertion order for these insert-only
    /// fixtures, so a malformed FIRST entry proves per-locale isolation.
    /// </summary>
    private static SkillStatus StatusForLocales(params (string Locale, SkillStatusState? State)[] entries) => new()
    {
        InteractionModel = new Dictionary<string, StatusManifest>(
            entries.Select(e => new KeyValuePair<string, StatusManifest>(
                e.Locale,
                new StatusManifest
                {
                    LastModified = e.State.HasValue ? new LastModifiedInformation { Status = e.State.Value } : null,
                }))),
    };

    private static Entities.User UserServing(SkillStatus status) =>
        UserServing(status, out _);

    /// <summary>
    /// The JF-722 refresh twin of <see cref="UserServing(SkillStatus)"/>: hands back
    /// the fake so the pins can assert the poll count and drive per-poll sequences.
    /// </summary>
    private static Entities.User UserServing(SkillStatus status, out FakeStatusSmapiManagement fake)
    {
        var user = TestHelpers.CreateSyncUser();
        fake = new FakeStatusSmapiManagement(status);
        user.SetSmapiManagementForTest(fake);
        return user;
    }

    /// <summary>The shared act for every clean-capture pin: a no-error
    /// observation for it-IT driven through the capture. SUCCEEDED by default;
    /// the JF-719 twin passes IN_PROGRESS (the freshly-PUT locale the
    /// no-settle-wait capture reads right after UpdateSkillAsync).</summary>
    private Task CaptureCleanAsync(SkillStatusState state = SkillStatusState.SUCCEEDED) =>
        CreateStartup().CaptureLocaleModelStatusesAsync(
            UserServing(StatusFor("it-IT", state)),
            "amzn1.ask.skill.test-id");

    /// <summary>Seeds the it-IT ledger row the capture will overwrite, returning
    /// the seeded timestamp for freshness assertions.</summary>
    private static DateTime SeedLocaleRow(string? error, string source = LibrarySyncService.CatalogSyncLedgerSource) =>
        SeedRow("it-IT", "SUCCEEDED", error, source);

    /// <summary>
    /// Seeds an arbitrary ledger row (the JF-722 refresh pins need the frozen
    /// IN_PROGRESS/Embedded shape the capture writes, plus non-family rows the
    /// refresh must not touch), returning the seeded timestamp for freshness
    /// assertions.
    /// </summary>
    private static DateTime SeedRow(string locale, string status, string? error, string source = "Embedded")
    {
        var seeded = DateTime.UtcNow.AddHours(-2);
        Plugin.Instance!.Configuration.SetLocaleModelStatus(locale, new LocaleModelStatus
        {
            Status = status,
            LastUpdated = seeded,
            Error = error,
            Source = source,
        });
        return seeded;
    }

    /// <summary>
    /// JF-710 core pin: the compound skip-gated-restart path is (freeze wrote
    /// the clause) + (restart with a version change runs the skill update and
    /// THIS capture) + (the startup re-sync is skipped, so nothing rewrites the
    /// row). A clean capture (SUCCEEDED, no build errors) must therefore not
    /// erase the catalog-sync diagnostics: the frozen clause survives, the
    /// foreign diagnostic a no-PUT run trailed survives WITHOUT its
    /// "previous: " framing, and the run-scoped "; no PUT this run" tail drops
    /// (a new skill version WAS pushed). Status and Source stay the capture's
    /// own: the fresh model build really did succeed.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CleanCapture_OverNoPutClauseRow_PreservesClauseAndForeignDropsTail()
    {
        string clause = $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}";
        string foreign = "canary mismatch: submitted 145 intents/900 samples but live model reports 144/899";
        SeedLocaleRow($"{clause}{LibrarySyncService.NoPutLedgerTail}{LibrarySyncService.PreviousLedgerDiagnosticPrefix}{foreign}");

        await CaptureCleanAsync();

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal("Embedded", row.Source);
        Assert.Equal($"{clause}; {foreign}", row.Error);

        // Code-review F1 pin: the capture writes Source "Embedded", so the
        // NEXT clean capture (another version bump, or the FAILED-manifest
        // trigger, while the startup re-sync stays skip-gated) must STILL
        // preserve: recognition is content-keyed (the clause marker), not
        // Source-keyed, or the clause would die exactly one restart later
        // than JF-710 was filed to fix.
        await CaptureCleanAsync();
        row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal($"{clause}; {foreign}", row!.Error);
    }

    /// <summary>
    /// JF-710 gate-marker tail: the PLAIN no-PUT row (clause + tail, no trailed
    /// foreign) must reduce to clause-only through the capture, and that
    /// clause-only product must be idempotent under a second capture (only the
    /// compound clause+tail+foreign shape and its double capture were pinned
    /// before; a future edit to the preserve that mishandles the tail-only
    /// strip or the marker-only idempotence would otherwise pass the suite).
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CleanCapture_OverPlainNoPutRow_ReducesToClauseAndStaysIdempotent()
    {
        string clause = $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}";
        SeedLocaleRow($"{clause}{LibrarySyncService.NoPutLedgerTail}");

        await CaptureCleanAsync();

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal(clause, row!.Error);

        // The clause-only product has no tail left to strip; a second capture
        // must be a Replace no-op, not a mutation.
        await CaptureCleanAsync();
        row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal(clause, row!.Error);
    }

    /// <summary>
    /// JF-710: the clean capture over a CLEAN row must stay byte-clean itself:
    /// no clause is invented, Error stays null, only the timestamp refreshes.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CleanCapture_OverCleanRow_LeavesErrorNull()
    {
        var seeded = SeedLocaleRow(null);

        await CaptureCleanAsync();

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal("Embedded", row.Source);
        Assert.Null(row.Error);
        Assert.True(row.LastUpdated > seeded, "the capture must still refresh the row's timestamp");
    }

    /// <summary>
    /// JF-719 twin of the compound pin: the same clean capture driven with the
    /// observed state IN_PROGRESS. The capture runs immediately after
    /// UpdateSkillAsync with NO per-locale settle-wait, so freshly-PUT locales
    /// legitimately read IN_PROGRESS (the settle-wait alternative was rejected:
    /// startup latency to protect a sub-case the content-keyed preserve already
    /// handles safely). The preserve must not depend on the build having
    /// settled: the clause survives, the trailed foreign survives un-framed,
    /// the run-scoped tail drops, and the row keeps the capture's own
    /// IN_PROGRESS status. Under the pre-JF-719 SUCCEEDED-only gate this exact
    /// world wrote Error=null and erased the clause (the filed residual).
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CleanInProgressCapture_OverNoPutClauseRow_PreservesClauseAndForeignDropsTail()
    {
        string clause = $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}";
        string foreign = "canary mismatch: submitted 145 intents/900 samples but live model reports 144/899";
        SeedLocaleRow($"{clause}{LibrarySyncService.NoPutLedgerTail}{LibrarySyncService.PreviousLedgerDiagnosticPrefix}{foreign}");

        await CaptureCleanAsync(SkillStatusState.IN_PROGRESS);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("IN_PROGRESS", row!.Status);
        Assert.Equal("Embedded", row.Source);
        Assert.Equal($"{clause}; {foreign}", row.Error);

        // Idempotence mirrors the SUCCEEDED pin's second capture: another
        // capture while the startup re-sync stays skip-gated must preserve the
        // content-keyed clause off the capture-written row.
        await CaptureCleanAsync(SkillStatusState.IN_PROGRESS);
        row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal($"{clause}; {foreign}", row!.Error);
    }

    /// <summary>
    /// JF-719: the gate is an allowlist (SUCCEEDED or IN_PROGRESS), so a FAILED
    /// observation WITHOUT an Errors array still replaces wholesale. This is
    /// the widened gate's guard: a naive "any no-errors capture preserves"
    /// widening would fire the preserve here and park the catalog clause on a
    /// row the diagnostics panel counts in failedModels, misattributing
    /// catalog state to a build failure. Error lands null (nothing of the
    /// capture's own to say; SMAPI sent no error details).
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_FailedCaptureWithoutErrors_OverClauseRow_ReplacesWholesale()
    {
        SeedLocaleRow($"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}");

        await CreateStartup().CaptureLocaleModelStatusesAsync(
            UserServing(StatusFor("it-IT", SkillStatusState.FAILED)),
            "amzn1.ask.skill.test-id");

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("FAILED", row!.Status);
        Assert.Null(row.Error);
    }

    /// <summary>
    /// JF-719: build errors replace wholesale regardless of the observed
    /// state; an IN_PROGRESS capture WITH its own errors is not clean, so the
    /// preserve must not fire and the fresh failure must not be masked.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_InProgressCaptureWithErrors_OverClauseRow_ReplacesWithOwnError()
    {
        SeedLocaleRow($"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}");

        var status = StatusFor(
            "it-IT",
            SkillStatusState.IN_PROGRESS,
            new[] { new InvocationError { Code = "INVALID_SKILL_PACKAGE", Message = "sample utterance is not unique" } });
        await CreateStartup().CaptureLocaleModelStatusesAsync(UserServing(status), "amzn1.ask.skill.test-id");

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("IN_PROGRESS", row!.Status);
        Assert.Equal("build errors: INVALID_SKILL_PACKAGE: sample utterance is not unique", row.Error);
    }

    /// <summary>
    /// JF-710: a capture WITH its own build errors still replaces the row
    /// wholesale with its own error; the preserve never masks a fresh failure.
    /// JF-722 rework F1: the own-error composition carries the
    /// ObservedBuildErrorsLedgerPrefix marker so the deferred refresh's
    /// clean-settle arm can tell it from the preserve's product.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_FailedCapture_OverClauseRow_ReplacesWithOwnError()
    {
        SeedLocaleRow($"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}");

        var status = StatusFor(
            "it-IT",
            SkillStatusState.FAILED,
            new[] { new InvocationError { Code = "INVALID_SKILL_PACKAGE", Message = "sample utterance is not unique" } });
        await CreateStartup().CaptureLocaleModelStatusesAsync(UserServing(status), "amzn1.ask.skill.test-id");

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("FAILED", row!.Status);
        Assert.Equal("build errors: INVALID_SKILL_PACKAGE: sample utterance is not unique", row.Error);
    }

    /// <summary>
    /// JF-710: only catalog-sync-authored rows preserve. A previous capture's
    /// own build error (Source "Embedded") describes a build the fresh capture
    /// just superseded, so a clean capture must still CLEAR it.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CleanCapture_OverStaleEmbeddedError_ClearsIt()
    {
        SeedLocaleRow("INVALID_SKILL_PACKAGE: stale prior build failure", source: "Embedded");

        await CaptureCleanAsync();

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Null(row.Error);
    }

    /// <summary>
    /// JF-710 (the JF-495 extension): a BARE foreign diagnostic from the
    /// catalog-sync PUT writer (a canary mismatch with no frozen types) has no
    /// clause and no tail, and survives the clean capture verbatim. It rides
    /// its Source label, so it survives ONE capture cycle only: unlike the
    /// freeze (durable state), a canary describes the model build it followed,
    /// which the next version-change push replaces.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CleanCapture_OverBareCanaryRow_PreservesItOnceThenClears()
    {
        const string canary = "canary mismatch: submitted 145 intents/900 samples but live model reports 144/899";
        SeedLocaleRow(canary);

        await CaptureCleanAsync();

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal(canary, row.Error);

        // The capture rewrote Source to "Embedded"; a bare diagnostic carries
        // no marker to key recognition on, so the SECOND clean capture clears
        // it (content-keyed preserve, code-review F1 boundary).
        await CaptureCleanAsync();
        row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Null(row!.Error);
    }

    /// <summary>
    /// JF-705 PUT-writer combined shape under the JF-710 preserve: a clause-led
    /// row with a canary behind it survives a clean capture unchanged (the
    /// decomposition must not lose the foreign diagnostic when no tail is
    /// present to strip).
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CleanCapture_OverClausePlusCanaryRow_PreservesBoth()
    {
        string clause = $"Artist + Album catalogs{LibrarySyncService.FrozenLedgerClauseMarker}";
        string foreign = "canary mismatch: submitted 145 intents/900 samples but live model reports 144/899";
        SeedLocaleRow($"{clause}; {foreign}");

        await CaptureCleanAsync();

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal($"{clause}; {foreign}", row!.Error);
    }

    // ------------------------------------------------------------------
    // JF-722 residual 2: per-locale null isolation in the capture.
    // ------------------------------------------------------------------

    /// <summary>
    /// JF-722 residual 2: a malformed per-locale entry (LastModified null, the
    /// freshly-created-skill shape where a locale is registered before its first
    /// build starts) must cost only its own row. Under the old unguarded
    /// dereference the NullReferenceException aborted the capture for every
    /// LATER locale in dictionary order under the whole-method catch: a silent
    /// partial capture the admin could not distinguish from a complete one. The
    /// malformed locale is skipped (no observation to record; its previous row
    /// stays the last settled truth) and both healthy locales after it still
    /// capture.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_MalformedLocaleEntry_SkipsItAndCapturesTheRest()
    {
        string deError = "previous settled diagnostic";
        var deSeeded = SeedRow("de-DE", "SUCCEEDED", deError, source: LibrarySyncService.CatalogSyncLedgerSource);
        var before = DateTime.UtcNow;

        var status = StatusForLocales(
            ("de-DE", null),
            ("it-IT", SkillStatusState.SUCCEEDED),
            ("en-US", SkillStatusState.SUCCEEDED));
        await CreateStartup().CaptureLocaleModelStatusesAsync(UserServing(status), "amzn1.ask.skill.test-id");

        // The malformed locale: skipped, previous row untouched.
        var de = Plugin.Instance!.Configuration.GetLocaleModelStatus("de-DE");
        Assert.NotNull(de);
        Assert.Equal("SUCCEEDED", de!.Status);
        Assert.Equal(deError, de.Error);
        Assert.Equal(deSeeded, de.LastUpdated);
        Assert.Equal(LibrarySyncService.CatalogSyncLedgerSource, de.Source);

        // Every later locale in dictionary order still captured.
        foreach (var locale in new[] { "it-IT", "en-US" })
        {
            var row = Plugin.Instance!.Configuration.GetLocaleModelStatus(locale);
            Assert.NotNull(row);
            Assert.Equal("SUCCEEDED", row!.Status);
            Assert.Equal("Embedded", row.Source);
            Assert.True(row.LastUpdated >= before, $"{locale} must be captured, not skipped by the aborted loop");
        }
    }

    /// <summary>
    /// JF-722 rework F3 (the derivation's capture half): the capture's return is
    /// the recapture-mode signal the pairing wrapper derives from. A healthy
    /// capture returns TRUE (rows written); the sparse-status shapes return
    /// FALSE: no interactionModel surface at all, and every locale entry still
    /// pre-build (null LastModified, rows skipped). Pinned both ways so the
    /// wrapper's derivation input cannot drift.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_ReturnsWhetherAnyRowWasWritten()
    {
        ClearLedger();

        // Healthy: rows written.
        bool healthy = await CreateStartup().CaptureLocaleModelStatusesAsync(
            UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED)), "amzn1.ask.skill.test-id");
        Assert.True(healthy);

        // Sparse: no interactionModel surface at all (freshly created skill, or
        // a transient sparse response after a deploy).
        var sparse = new SkillStatus();
        bool sparseWrote = await CreateStartup().CaptureLocaleModelStatusesAsync(
            UserServing(sparse), "amzn1.ask.skill.test-id");
        Assert.False(sparseWrote);

        // All entries pre-build: every locale skipped, zero rows written.
        ClearLedger();
        bool allPreBuild = await CreateStartup().CaptureLocaleModelStatusesAsync(
            UserServing(StatusForLocales(("it-IT", null), ("en-US", null))), "amzn1.ask.skill.test-id");
        Assert.False(allPreBuild);
        Assert.Null(Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT"));
    }

    // ------------------------------------------------------------------
    // JF-722 residual 1: the deferred IN_PROGRESS status refresh.
    // ------------------------------------------------------------------

    /// <summary>The shared act for the refresh pins: the deferred worker with no
    /// delays and a single poll unless a test extends it.</summary>
    private Task RefreshAsync(Entities.User user, int maxPolls = 1, bool captureWroteNoRows = false, CancellationToken cancellationToken = default) =>
        CreateStartup().RefreshInProgressLocaleStatusesAsync(
            user, "amzn1.ask.skill.test-id", cancellationToken,
            initialDelay: TimeSpan.Zero, pollInterval: TimeSpan.Zero, maxPolls: maxPolls, captureWroteNoRows: captureWroteNoRows);

    /// <summary>
    /// Empties the shared ledger (the creation-path pins need the empty-ledger
    /// world a freshly created skill leaves behind: zero rows anywhere).
    /// </summary>
    private static void ClearLedger() => Plugin.Instance!.Configuration.LocaleModelStatuses.Clear();

    /// <summary>
    /// JF-722 core pin: the settle-and-REWRITE refresh turns the frozen
    /// IN_PROGRESS capture row into the settled truth (Status SUCCEEDED feeds
    /// the panel's ModelsDeployed checklist) while carrying the row's Error
    /// VERBATIM: the capture already ran the JF-710/JF-719 preserve when it
    /// wrote the row, and re-running it here would re-decompose an
    /// already-decomposed product. A visited locale whose ledger row is not
    /// the capture family (settled, not IN_PROGRESS) is untouched.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_SettledObservation_RewritesStatusAndCarriesErrorVerbatim()
    {
        string clause = $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}";
        string carried = $"{clause}; canary mismatch: submitted 145 intents/900 samples but live model reports 144/899";
        var itSeeded = SeedRow("it-IT", "IN_PROGRESS", carried);
        var enSeeded = SeedRow("en-US", "SUCCEEDED", "settled row, not the refresh family");

        var status = StatusForLocales(("it-IT", SkillStatusState.SUCCEEDED), ("en-US", SkillStatusState.SUCCEEDED));
        var user = UserServing(status, out var fake);

        await RefreshAsync(user);

        var it = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(it);
        Assert.Equal("SUCCEEDED", it!.Status);
        Assert.Equal(carried, it.Error);
        Assert.Equal("Embedded", it.Source);
        Assert.True(it.LastUpdated > itSeeded, "the refresh must refresh the row's timestamp");

        var en = Plugin.Instance!.Configuration.GetLocaleModelStatus("en-US");
        Assert.NotNull(en);
        Assert.Equal("SUCCEEDED", en!.Status);
        Assert.Equal(enSeeded, en.LastUpdated);

        Assert.Equal(1, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722: a FAILED settle with build errors replaces the row wholesale,
    /// mirroring the capture's own branch semantics (a build-failure
    /// observation never carries a catalog clause that had nothing to do with
    /// it): the preserved clause is dropped and the fresh failure surfaces,
    /// marker-prefixed like the capture's own arm (rework F1's marker family).
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_FailedObservationWithErrors_ReplacesWholesale()
    {
        SeedRow("it-IT", "IN_PROGRESS", $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}");

        var status = StatusFor(
            "it-IT",
            SkillStatusState.FAILED,
            new[] { new InvocationError { Code = "INVALID_SKILL_PACKAGE", Message = "sample utterance is not unique" } });
        var user = UserServing(status, out _);

        await RefreshAsync(user);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("FAILED", row!.Status);
        Assert.Equal("build errors: INVALID_SKILL_PACKAGE: sample utterance is not unique", row.Error);
    }

    /// <summary>
    /// JF-722 rework F1, half A (stale observed errors do NOT survive a clean
    /// settle), END TO END through both writers: the capture observes
    /// IN_PROGRESS WITH an Errors array (the unverified-SMAPI shape where the
    /// array may carry the PREVIOUS build's errors) and composes the row's Error
    /// from them (marker-prefixed, wholesale); the refresh later observes the
    /// clean SUCCEEDED settle and must DROP that text (it describes the
    /// error-carrying observation, not the settled build) instead of carrying
    /// it onto the green row.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_CleanSettle_OverStaleObservedErrorsRow_DropsThem()
    {
        // Capture half: IN_PROGRESS + own errors over any previous row.
        var captureStatus = StatusFor(
            "it-IT",
            SkillStatusState.IN_PROGRESS,
            new[] { new InvocationError { Code = "INVALID_SKILL_PACKAGE", Message = "previous build error" } });
        await CreateStartup().CaptureLocaleModelStatusesAsync(UserServing(captureStatus), "amzn1.ask.skill.test-id");

        var frozen = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(frozen);
        Assert.Equal("IN_PROGRESS", frozen!.Status);
        Assert.Equal("build errors: INVALID_SKILL_PACKAGE: previous build error", frozen.Error);

        // Refresh half: clean settle.
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out _);
        await RefreshAsync(user);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Null(row.Error);
    }

    /// <summary>
    /// JF-722 rework F1, half B (the preserve's product DOES survive), END TO
    /// END through both writers: a clean IN_PROGRESS capture preserves the
    /// frozen clause + foreign diagnostic onto the frozen row (JF-719), and the
    /// refresh's clean settle carries it VERBATIM onto the SUCCEEDED row.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_CleanSettle_OverPreservedClauseRow_EndToEnd_CarriesIt()
    {
        string clause = $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}";
        string foreign = "canary mismatch: submitted 145 intents/900 samples but live model reports 144/899";
        SeedLocaleRow($"{clause}{LibrarySyncService.NoPutLedgerTail}{LibrarySyncService.PreviousLedgerDiagnosticPrefix}{foreign}");

        // Capture half: clean IN_PROGRESS, preserve fires.
        await CreateStartup().CaptureLocaleModelStatusesAsync(
            UserServing(StatusFor("it-IT", SkillStatusState.IN_PROGRESS)), "amzn1.ask.skill.test-id");

        var frozen = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(frozen);
        Assert.Equal($"{clause}; {foreign}", frozen!.Error);

        // Refresh half: clean settle carries it.
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out _);
        await RefreshAsync(user);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal($"{clause}; {foreign}", row.Error);
    }

    /// <summary>
    /// JF-722 rework F3: when the paired capture wrote ZERO rows (the sparse
    /// status of a freshly created skill), the plain pre-check would exit
    /// before any poll; the derived recapture mode's first poll RE-CAPTURES
    /// after the settle window and writes the first-build rows (here already
    /// settled), then the rewrite pass finds no family rows and the refresh
    /// exits. Two status GETs (recapture + rewrite).
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_RecaptureMode_EmptyLedger_WritesFirstBuildRows()
    {
        ClearLedger();

        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);

        await RefreshAsync(user, captureWroteNoRows: true);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal("Embedded", row.Source);
        Assert.Equal(2, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722 rework F3: a transiently failing recapture (the first poll's GET
    /// throws; the capture swallows it internally and reports writing nothing)
    /// is RETRIED on the next poll instead of leaving the creation refresh
    /// inert; once the recapture writes the in-flight rows, the normal rewrite
    /// loop settles them.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_RecaptureMode_RetriesFailedRecaptureThenSettles()
    {
        ClearLedger();

        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);
        fake.FailNextCalls = 1;
        fake.SequenceToServe = new Queue<SkillStatus>(new[]
        {
            StatusFor("it-IT", SkillStatusState.IN_PROGRESS), // poll 2's recapture
            StatusFor("it-IT", SkillStatusState.IN_PROGRESS), // poll 2's rewrite
            StatusFor("it-IT", SkillStatusState.SUCCEEDED),   // poll 3's rewrite
        });

        await RefreshAsync(user, maxPolls: 3, captureWroteNoRows: true);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal(4, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722 rework F3 boundary: WITHOUT the creation mode, an empty ledger is
    /// the nothing-to-refresh world (the version-bump capture just wrote settled
    /// rows elsewhere or nothing froze) and the pre-check exits before any
    /// network call.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_EmptyLedger_NoRecapture_MakesNoNetworkCall()
    {
        ClearLedger();

        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);

        await RefreshAsync(user, maxPolls: 2);

        Assert.Equal(0, fake.GetStatusCalls);
        Assert.Null(Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT"));
    }

    /// <summary>
    /// JF-722 rework F2: a cancellation landing DURING a poll's status GET (the
    /// startup torn down while the GET was in flight) must abort the poll at the
    /// checkpoint after the await, BEFORE any ledger rewrite or config save: the
    /// worker exits quietly and the row stays exactly as the capture wrote it.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_CancelledDuringPollGet_LeavesRowUntouched()
    {
        SeedRow("it-IT", "IN_PROGRESS", null);

        using var cts = new CancellationTokenSource();
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);
        fake.OnGetStatus = cts.Cancel;

        await RefreshAsync(user, cancellationToken: cts.Token);

        Assert.Equal(1, fake.GetStatusCalls);
        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("IN_PROGRESS", row!.Status);
        Assert.Null(row.Error);
    }

    /// <summary>
    /// JF-722: a FAILED settle WITHOUT error details clears the Error (the
    /// capture's FAILED-without-errors twin: wholesale, nothing of the
    /// observation's own to say).
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_FailedObservationWithoutErrors_ClearsError()
    {
        SeedRow("it-IT", "IN_PROGRESS", $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}");

        var user = UserServing(StatusFor("it-IT", SkillStatusState.FAILED), out _);

        await RefreshAsync(user);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("FAILED", row!.Status);
        Assert.Null(row.Error);
    }

    /// <summary>
    /// JF-722: a locale still IN_PROGRESS at poll N is left for poll N+1 within
    /// the budget (the settle window is a background wait, not a skipped one).
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_StillInProgress_RepollsUntilSettled()
    {
        SeedRow("it-IT", "IN_PROGRESS", null);
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);
        fake.SequenceToServe = new Queue<SkillStatus>(new[]
        {
            StatusFor("it-IT", SkillStatusState.IN_PROGRESS),
            StatusFor("it-IT", SkillStatusState.SUCCEEDED),
        });

        await RefreshAsync(user, maxPolls: 2);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal(2, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722 (code-review F5): a transient poll failure (429/5xx on the status
    /// GET) must not spend the whole budget: the next poll retries and the row
    /// still settles within the same refresh.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_TransientPollFailure_RetriesOnNextPollAndSettles()
    {
        SeedRow("it-IT", "IN_PROGRESS", null);
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);
        fake.FailNextCalls = 1;

        await RefreshAsync(user, maxPolls: 2);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal(2, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722: rows still IN_PROGRESS after the budget are left exactly as the
    /// capture wrote them (healthy-neutral; the weekly sync is the backstop),
    /// not re-frozen with a fresh timestamp.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_BudgetExhausted_LeavesRowIntact()
    {
        string clause = $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}";
        var seeded = SeedRow("it-IT", "IN_PROGRESS", clause);

        var user = UserServing(StatusFor("it-IT", SkillStatusState.IN_PROGRESS), out var fake);

        await RefreshAsync(user, maxPolls: 2);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("IN_PROGRESS", row!.Status);
        Assert.Equal(clause, row.Error);
        Assert.Equal(seeded, row.LastUpdated);
        Assert.Equal(2, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722: the pre-check exits before any delay or network call when no
    /// IN_PROGRESS capture-family row exists (captures whose every observation
    /// settled schedule a refresh that costs nothing).
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_NothingInProgress_MakesNoNetworkCall()
    {
        SeedRow("it-IT", "SUCCEEDED", null);
        var user = UserServing(StatusFor("it-IT", SkillStatusState.IN_PROGRESS), out var fake);

        await RefreshAsync(user, maxPolls: 3);

        Assert.Equal(0, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722 family boundary: an IN_PROGRESS row NOT authored by the capture
    /// (the catalog-sync source label; the sync writers never write IN_PROGRESS
    /// today, so this is the defensive/hand-edited shape) is not the refresh's
    /// to rewrite, and its presence does not even spend a poll.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_SyncAuthoredInProgressRow_IsNotTheRefreshFamily()
    {
        var seeded = SeedRow(
            "it-IT", "IN_PROGRESS",
            $"Artist catalog{LibrarySyncService.FrozenLedgerClauseMarker}",
            source: LibrarySyncService.CatalogSyncLedgerSource);

        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);

        await RefreshAsync(user);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("IN_PROGRESS", row!.Status);
        Assert.Equal(seeded, row.LastUpdated);
        Assert.Equal(0, fake.GetStatusCalls);
    }

    /// <summary>
    /// JF-722: shutdown racing the refresh (the startup's linked token cancels
    /// the initial delay) exits quietly without polling; the frozen rows keep
    /// their healthy-neutral status for the next capture-and-refresh cycle.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_CancelledDuringDelay_ExitsQuietlyWithoutPolling()
    {
        SeedRow("it-IT", "IN_PROGRESS", null);
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await CreateStartup().RefreshInProgressLocaleStatusesAsync(
            user, "amzn1.ask.skill.test-id", cts.Token,
            initialDelay: TimeSpan.FromHours(1), pollInterval: TimeSpan.Zero, maxPolls: 1);

        Assert.Equal(0, fake.GetStatusCalls);
        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("IN_PROGRESS", row!.Status);
    }

    /// <summary>
    /// JF-722 rework RC6: the capture's OWN after-GET cancellation checkpoint:
    /// a stop landing while the capture's GET is in flight must throw OCE out
    /// of the capture (token-state-filtered rethrow) BEFORE any ledger write,
    /// leaving the existing rows untouched.
    /// </summary>
    [Fact]
    public async Task CaptureLocaleModelStatusesAsync_CancelledDuringGet_WritesNothing()
    {
        var seeded = SeedRow("it-IT", "SUCCEEDED", null);

        using var cts = new CancellationTokenSource();
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);
        fake.OnGetStatus = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateStartup().CaptureLocaleModelStatusesAsync(user, "amzn1.ask.skill.test-id", cts.Token));

        Assert.Equal(1, fake.GetStatusCalls);
        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal(seeded, row!.LastUpdated);
    }

    /// <summary>
    /// JF-722 rework RC6: the recapture leg's cancellation boundary: a stop
    /// landing during the recapture's GET exits the refresh quietly with NO
    /// rows written (the capture's checkpoint fired before its loop).
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_RecaptureMode_CancelledDuringRecaptureGet_ExitsQuietlyWithNoRows()
    {
        ClearLedger();

        using var cts = new CancellationTokenSource();
        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);
        fake.OnGetStatus = cts.Cancel;

        await RefreshAsync(user, captureWroteNoRows: true, cancellationToken: cts.Token);

        Assert.Equal(1, fake.GetStatusCalls);
        Assert.Null(Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT"));
    }

    /// <summary>
    /// JF-722 rework RC1: an HttpClient TIMEOUT surfaces as a
    /// TaskCanceledException (an OCE subclass) with NO cancellation requested;
    /// the token-state filters must treat it as a transient poll failure
    /// (warn, return false, retry the recapture on the next poll) instead of
    /// letting it silently kill the whole refresh through the quiet OCE exit.
    /// </summary>
    [Fact]
    public async Task RefreshInProgressLocaleStatusesAsync_RecaptureMode_TimeoutShapedCancellation_RetriesAndWrites()
    {
        ClearLedger();

        var user = UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED), out var fake);
        fake.FailNextCalls = 1;
        fake.FailNextCallsException = new TaskCanceledException("the operation was canceled", new TimeoutException("100s HttpClient default timeout"));

        await RefreshAsync(user, maxPolls: 2, captureWroteNoRows: true);

        var row = Plugin.Instance!.Configuration.GetLocaleModelStatus("it-IT");
        Assert.NotNull(row);
        Assert.Equal("SUCCEEDED", row!.Status);
        Assert.Equal(4, fake.GetStatusCalls);
    }

    /// <summary>
    /// Serves a canned <see cref="SkillStatus"/> without network: the capture
    /// path reads exactly one endpoint (GetSkillStatusAsync), which is virtual
    /// for this seam (the JF-366 SetSmapiManagementForTest pattern). JF-722
    /// adds the call counter and the optional per-call sequence the refresh
    /// pins drive (the refresh polls the same endpoint within its budget).
    /// </summary>
    private sealed class FakeStatusSmapiManagement : SmapiManagement
    {
        public FakeStatusSmapiManagement(SkillStatus status)
            : base(TestHelpers.CreateTestDeviceToken(), NullLoggerFactory.Instance)
        {
            StatusToServe = status;
        }

        public SkillStatus StatusToServe { get; private set; }

        /// <summary>How many GetSkillStatusAsync calls were served.</summary>
        public int GetStatusCalls { get; private set; }

        /// <summary>
        /// When non-empty, each call dequeues its next status; once empty the
        /// last StatusToServe keeps serving (the budget-exhaustion world).
        /// </summary>
        public Queue<SkillStatus>? SequenceToServe { get; set; }

        /// <summary>
        /// When positive, the next that many calls throw (before any sequence
        /// dequeue) the exception in <see cref="FailNextCallsException"/>, so
        /// the refresh's per-poll retry can be pinned for both the 429/5xx
        /// shape and the HttpClient-timeout shape.
        /// </summary>
        public int FailNextCalls { get; set; }

        /// <summary>
        /// The exception the fail-next arm throws. Default: the transient
        /// 429/5xx HttpRequestException shape; the timeout pin overrides it
        /// with a TaskCanceledException wrapping a TimeoutException, the exact
        /// surface shape an HttpClient default-timeout GET produces (an OCE
        /// subclass with NO cancellation requested).
        /// </summary>
        public Exception FailNextCallsException { get; set; } = new HttpRequestException("HTTP 429 Too Many Requests (fake)");

        /// <summary>
        /// When set, invoked at the start of every call BEFORE serving: the
        /// mid-poll cancellation pin (JF-722 rework F2) cancels the worker's
        /// token here, simulating the startup torn down while a poll's GET is
        /// in flight, and the still-served status proves the checkpoint after
        /// the await is what stops the poll.
        /// </summary>
        public Action? OnGetStatus { get; set; }

        public override Task<SkillStatus> GetSkillStatusAsync(string skillId)
        {
            GetStatusCalls++;
            OnGetStatus?.Invoke();
            if (FailNextCalls > 0)
            {
                FailNextCalls--;
                throw FailNextCallsException;
            }

            if (SequenceToServe is { Count: > 0 })
            {
                StatusToServe = SequenceToServe.Dequeue();
            }

            return Task.FromResult(StatusToServe);
        }
    }
}
