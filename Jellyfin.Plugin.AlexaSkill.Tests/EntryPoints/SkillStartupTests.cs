using System;
using System.Collections.Generic;
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

    private static Entities.User UserServing(SkillStatus status)
    {
        var user = TestHelpers.CreateSyncUser();
        user.SetSmapiManagementForTest(new FakeStatusSmapiManagement(status));
        return user;
    }

    /// <summary>The shared act for every clean-capture pin: a SUCCEEDED, no-error
    /// observation for it-IT driven through the capture.</summary>
    private Task CaptureCleanAsync() =>
        CreateStartup().CaptureLocaleModelStatusesAsync(
            UserServing(StatusFor("it-IT", SkillStatusState.SUCCEEDED)),
            "amzn1.ask.skill.test-id");

    /// <summary>Seeds the it-IT ledger row the capture will overwrite, returning
    /// the seeded timestamp for freshness assertions.</summary>
    private static DateTime SeedLocaleRow(string? error, string source = LibrarySyncService.CatalogSyncLedgerSource)
    {
        var seeded = DateTime.UtcNow.AddHours(-2);
        Plugin.Instance!.Configuration.SetLocaleModelStatus("it-IT", new LocaleModelStatus
        {
            Status = "SUCCEEDED",
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

    /// <summary>
    /// Serves a canned <see cref="SkillStatus"/> without network: the capture
    /// path reads exactly one endpoint (GetSkillStatusAsync), which is virtual
    /// for this seam (the JF-366 SetSmapiManagementForTest pattern).
    /// </summary>
    private sealed class FakeStatusSmapiManagement : SmapiManagement
    {
        public FakeStatusSmapiManagement(SkillStatus status)
            : base(TestHelpers.CreateTestDeviceToken(), NullLoggerFactory.Instance)
        {
            StatusToServe = status;
        }

        public SkillStatus StatusToServe { get; }

        public override Task<SkillStatus> GetSkillStatusAsync(string skillId) => Task.FromResult(StatusToServe);
    }
}
