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
        Assert.Equal("INVALID_SKILL_PACKAGE: sample utterance is not unique", row.Error);
    }

    /// <summary>
    /// JF-710: a capture WITH its own build errors still replaces the row
    /// wholesale with its own error; the preserve never masks a fresh failure.
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
        Assert.Equal("INVALID_SKILL_PACKAGE: sample utterance is not unique", row.Error);
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

    // ------------------------------------------------------------------
    // JF-722 residual 1: the deferred IN_PROGRESS status refresh.
    // ------------------------------------------------------------------

    /// <summary>The shared act for the refresh pins: the deferred worker with no
    /// delays and a single poll unless a test extends it.</summary>
    private Task RefreshAsync(Entities.User user, int maxPolls = 1) =>
        CreateStartup().RefreshInProgressLocaleStatusesAsync(
            user, "amzn1.ask.skill.test-id", CancellationToken.None,
            initialDelay: TimeSpan.Zero, pollInterval: TimeSpan.Zero, maxPolls: maxPolls);

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
    /// it): the preserved clause is dropped and the fresh failure surfaces.
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
        Assert.Equal("INVALID_SKILL_PACKAGE: sample utterance is not unique", row.Error);
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
        /// When positive, the next that many calls throw a transient
        /// HttpRequestException (the 429/5xx poll-failure shape) before any
        /// sequence dequeue, so the refresh's per-poll retry can be pinned.
        /// </summary>
        public int FailNextCalls { get; set; }

        public override Task<SkillStatus> GetSkillStatusAsync(string skillId)
        {
            GetStatusCalls++;
            if (FailNextCalls > 0)
            {
                FailNextCalls--;
                throw new HttpRequestException("HTTP 429 Too Many Requests (fake)");
            }

            if (SequenceToServe is { Count: > 0 })
            {
                StatusToServe = SequenceToServe.Dequeue();
            }

            return Task.FromResult(StatusToServe);
        }
    }
}
