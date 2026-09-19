#nullable enable

using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Diagnostics;

/// <summary>
/// JF-328 setup/health panel: the endpoint assembles the checklist and the
/// per-user rows from real config state WITHOUT exposing secrets (tokens and
/// client secrets reduce to booleans), and the counters expose when the last
/// Alexa request was seen.
/// </summary>
[Collection("Plugin")]
public class SetupHealthPanelTests : PluginTestBase
{
    private readonly RequestCounters _counters = new();

    public SetupHealthPanelTests()
    {
        // GetPanel reads Plugin.Instance.Configuration (and the connectivity
        // checker the same); a real Plugin instance is required.
        TestHelpers.EnsureRealPlugin();
    }

    [Fact]
    public void LastRequestAt_NullUntilFirstRequest_ThenTracksRequests()
    {
        Assert.Null(_counters.LastRequestAt);

        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);
        _counters.IncrementRequests();

        Assert.NotNull(_counters.LastRequestAt);
        Assert.True(_counters.LastRequestAt!.Value >= before, "LastRequestAt must update on IncrementRequests");
    }

    [Fact]
    public async Task Panel_ReportsChecklistUserRowAndNoSecrets()
    {
        var config = Plugin.Instance!.Configuration;
        var user = new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = Guid.NewGuid(),
            JellyfinToken = "secret-jellyfin-token",
        };
        user.UserSkill = new UserSkill
        {
            SkillId = "amzn1.ask.skill.123",
            UserSkillStatus = UserSkillStatus.Ready,
            InvocationName = "mia collezione",
        };
        config.Users.Add(user);
        config.SetLocaleModelStatus("it-IT", new LocaleModelStatus { Status = "Succeeded", LastUpdated = DateTime.UtcNow });
        try
        {
            DiagnosticsController controller = CreateController();
            ActionResult result = await controller.GetPanel().ConfigureAwait(false);

            var json = Assert.IsType<JsonResult>(result);
            var payload = System.Text.Json.JsonSerializer.Serialize(json.Value!);

            var value = json.Value!;
            var checklist = value.GetType().GetProperty("Checklist")!.GetValue(value)!;
            Assert.True((bool)checklist.GetType().GetProperty("SkillCreated")!.GetValue(checklist)!);
            Assert.True((bool)checklist.GetType().GetProperty("AccountLinked")!.GetValue(checklist)!);
            Assert.True((bool)checklist.GetType().GetProperty("ModelsDeployed")!.GetValue(checklist)!);

            // Secrets must never ride the payload (AC#5): the real token value is
            // absent; its boolean shadow is present.
            Assert.DoesNotContain("secret-jellyfin-token", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("LwaClientSecret", payload, StringComparison.Ordinal);
            Assert.Contains("amzn1.ask.skill.123", payload, StringComparison.Ordinal);
        }
        finally
        {
            config.Users.Remove(user);
            config.LocaleModelStatuses.Clear();
        }
    }

    private DiagnosticsController CreateController()
        => new(_counters, new JellyfinConnectivityChecker(LoggerFactory.Create(b => { }).CreateLogger<JellyfinConnectivityChecker>()));
}
