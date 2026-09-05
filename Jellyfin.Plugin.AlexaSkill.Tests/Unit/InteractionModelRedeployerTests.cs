using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Management.InteractionModel;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Direct coverage of InteractionModelRedeployer.RedeployAsync's poll-and-report logic.
/// Added for JF-366: this logic had no unit tests (the controller tests mock
/// IInteractionModelRedeployer), so a PR #14 reporting bug — a scoped rebuild reported
/// stale statuses from untouched locales — shipped and was only caught by live testing.
/// </summary>
[Collection("Plugin")]
public class InteractionModelRedeployerTests : PluginTestBase
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    public InteractionModelRedeployerTests()
    {
        EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            cfg => { },
            "alexa-redeployer-test");
        Plugin.Instance!.ManifestSkill = new ManifestSkill(
            "Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json",
            "https://example.com",
            global::Alexa.NET.Management.SslCertificateType.Wildcard);
    }

    /// <summary>
    /// A scoped (single-locale) rebuild must report ONLY the deployed locale, even when
    /// the skill's poll status includes other locales. This is the exact PR #14 regression.
    /// </summary>
    [Fact]
    public async Task ScopedRebuild_ReportsOnlyDeployedLocale()
    {
        var user = CreateUserWithFakeSmapi(BuildStatus(new (string, SkillStatusState)[]
        {
            ("en-US", SkillStatusState.SUCCEEDED),   // deployed
            ("de-DE", SkillStatusState.SUCCEEDED),   // untouched, stale
            ("it-IT", SkillStatusState.SUCCEEDED)    // untouched, stale
        }));

        var redeployer = new InteractionModelRedeployer(_loggerFactory.CreateLogger<InteractionModelRedeployer>());
        var result = await redeployer.RedeployAsync(user, string.Empty, CancellationToken.None, "en-US");

        Assert.True(result.Success);
        Assert.Single(result.Locales);
        Assert.True(result.Locales.ContainsKey("en-US"));
        Assert.Equal(1, result.SucceededCount);
    }

    /// <summary>
    /// An all-locales (null filter) rebuild reports every locale in the poll status.
    /// </summary>
    [Fact]
    public async Task AllLocalesRebuild_ReportsAllLocales()
    {
        var user = CreateUserWithFakeSmapi(BuildStatus(new (string, SkillStatusState)[]
        {
            ("en-US", SkillStatusState.SUCCEEDED),
            ("de-DE", SkillStatusState.SUCCEEDED)
        }));

        var redeployer = new InteractionModelRedeployer(_loggerFactory.CreateLogger<InteractionModelRedeployer>());
        var result = await redeployer.RedeployAsync(user, string.Empty, CancellationToken.None, localeFilter: null);

        Assert.True(result.Success);
        Assert.Equal(2, result.Locales.Count);
        Assert.True(result.Locales.ContainsKey("en-US"));
        Assert.True(result.Locales.ContainsKey("de-DE"));
    }

    /// <summary>
    /// The core regression guard: a FAILED status on a locale that was NOT deployed must
    /// not pollute a scoped rebuild's success/result. Before JF-366 the poll loop reported
    /// all locales, so a stale FAILED on an untouched locale made a successful 1-locale
    /// rebuild report "1 succeeded, 1 failed" with Success=false.
    /// </summary>
    [Fact]
    public async Task ScopedRebuild_StaleFailureOnUndeployedLocaleDoesNotPolluteResult()
    {
        var user = CreateUserWithFakeSmapi(BuildStatus(new (string, SkillStatusState)[]
        {
            ("en-US", SkillStatusState.SUCCEEDED),   // deployed — succeeded
            ("fr-FR", SkillStatusState.FAILED)       // undeployed — stale failure must be ignored
        }));

        var redeployer = new InteractionModelRedeployer(_loggerFactory.CreateLogger<InteractionModelRedeployer>());
        var result = await redeployer.RedeployAsync(user, string.Empty, CancellationToken.None, "en-US");

        Assert.True(result.Success);                 // not dragged false by fr-FR's stale FAILED
        Assert.Single(result.Locales);               // fr-FR not reported
        Assert.DoesNotContain("fr-FR", result.Locales.Keys);
        Assert.Equal(1, result.SucceededCount);
    }

    /// <summary>
    /// JF-495 canary: when the live model echoes the submitted payload the canary
    /// logs OK and no ERROR is emitted.
    /// </summary>
    [Fact]
    public async Task Redeploy_CanaryMatchesLiveModel_LogsNoError()
    {
        var loggerMock = new Mock<ILogger<InteractionModelRedeployer>>();
        var user = CreateUserWithFakeSmapi(BuildStatus(new (string, SkillStatusState)[]
        {
            ("en-US", SkillStatusState.SUCCEEDED)
        }));

        var redeployer = new InteractionModelRedeployer(loggerMock.Object);
        var result = await redeployer.RedeployAsync(user, string.Empty, CancellationToken.None, "en-US");

        Assert.True(result.Success);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("canary MISMATCH", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// JF-495 canary: when the live model does NOT match what was submitted (a
    /// racing deploy replaced it), the redeployer must log an ERROR naming both
    /// count pairs. The deploy result itself stays successful (log-only, no rollback).
    /// </summary>
    [Fact]
    public async Task Redeploy_LiveModelDiffers_CanaryMismatchLogsError()
    {
        var loggerMock = new Mock<ILogger<InteractionModelRedeployer>>();
        var user = CreateUserWithFakeSmapi(BuildStatus(new (string, SkillStatusState)[]
        {
            ("en-US", SkillStatusState.SUCCEEDED)
        }));

        // One intent, no samples: guaranteed to differ from the embedded en-US model.
        SetLiveModelOverride(user, new SkillInteractionContainer
        {
            InteractionModel = new SkillInteraction
            {
                Language = new Language
                {
                    InvocationName = "jellyfin player",
                    IntentTypes = new[] { new IntentType { Name = "OnlyIntent", Samples = Array.Empty<string>() } }
                }
            }
        });

        var redeployer = new InteractionModelRedeployer(loggerMock.Object);
        var result = await redeployer.RedeployAsync(user, string.Empty, CancellationToken.None, "en-US");

        Assert.True(result.Success); // canary is log-only, it must not fail the deploy
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("canary MISMATCH", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ---- helpers ----

    private static Entities.User CreateUserWithFakeSmapi(SkillStatus status)
    {
        var user = new Entities.User
        {
            Id = Guid.NewGuid(),
            UserSkill = new UserSkill
            {
                SkillId = "amzn1.ask.skill.test",
                InvocationName = string.Empty,
                UserSkillStatus = UserSkillStatus.Ready
            },
            SmapiDeviceToken = CreateTestDeviceToken()
        };
        var fake = new FakeSmapiManagement(status);
        user.SetSmapiManagementForTest(fake);
        return user;
    }

    /// <summary>
    /// Sets the canary's live-model override on the user's fake SMAPI instance.
    /// Null (the default) means "echo back what was last PUT", the canary-OK path.
    /// </summary>
    private static void SetLiveModelOverride(Entities.User user, SkillInteractionContainer? liveModel)
    {
        var fake = (FakeSmapiManagement)user.SmapiManagement!;
        fake.LiveModelOverride = liveModel;
    }

    private static SkillStatus BuildStatus(IEnumerable<(string Locale, SkillStatusState State)> locales)
    {
        var status = new SkillStatus { InteractionModel = new Dictionary<string, StatusManifest>() };
        foreach (var (locale, state) in locales)
        {
            status.InteractionModel[locale] = new StatusManifest
            {
                LastModified = new LastModifiedInformation { Status = state }
            };
        }

        return status;
    }

    /// <summary>
    /// Fake SmapiManagement: returns a controlled SkillStatus and a no-op update result,
    /// avoiding all SMAPI network calls. Overrides the virtual seam methods. The
    /// interaction-model GET (JF-495 canary) echoes back the model that was last PUT,
    /// unless <see cref="LiveModelOverride"/> is set.
    /// </summary>
    private sealed class FakeSmapiManagement : SmapiManagement
    {
        private readonly SkillStatus _status;
        private Collection<SkillInteractionModel>? _lastUpdateModels;

        public FakeSmapiManagement(SkillStatus status)
            : base(CreateTestDeviceToken(), LoggerFactory.Create(b => { }))
        {
            _status = status;
        }

        public SkillInteractionContainer? LiveModelOverride { get; set; }

        public override Task<SkillStatus> GetSkillStatusAsync(string skillId)
            => Task.FromResult(_status);

        public override Task<Dictionary<string, string>> UpdateSkillAsync(
            string skillId, ManifestSkill manifestSkill,
            Collection<SkillInteractionModel> interactionModels)
        {
            _lastUpdateModels = interactionModels;
            return Task.FromResult(new Dictionary<string, string>());
        }

        public override Task<SkillInteractionContainer> GetInteractionModelAsync(string skillId, string locale)
        {
            if (LiveModelOverride != null)
            {
                return Task.FromResult(LiveModelOverride);
            }

            var match = _lastUpdateModels?.FirstOrDefault(m =>
                string.Equals(m.Locale, locale, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<SkillInteractionContainer>(match ?? new SkillInteractionContainer());
        }
    }
}
