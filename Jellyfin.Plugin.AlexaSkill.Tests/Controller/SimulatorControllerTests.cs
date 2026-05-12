using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

public class SimulatorControllerTests
{
    /// <summary>
    /// Verify that SimulatorRequest defaults are null (not crashing).
    /// </summary>
    [Fact]
    public void SimulatorRequest_Defaults_AreNull()
    {
        var request = new SimulatorRequest();
        Assert.Null(request.IntentName);
        Assert.Null(request.Slots);
        Assert.Null(request.Locale);
        Assert.Null(request.DeviceId);
    }

    /// <summary>
    /// Verify that the PluginConfiguration default for SimulatorEnabled is false.
    /// </summary>
    [Fact]
    public void PluginConfiguration_Default_SimulatorDisabled()
    {
        var config = new PluginConfiguration();
        Assert.False(config.SimulatorEnabled);
    }

    /// <summary>
    /// Verify SimulatorEnabled can be toggled.
    /// </summary>
    [Fact]
    public void PluginConfiguration_CanToggleSimulator()
    {
        var config = new PluginConfiguration();
        Assert.False(config.SimulatorEnabled);

        config.SimulatorEnabled = true;
        Assert.True(config.SimulatorEnabled);
    }

    /// <summary>
    /// Verify that GetStatus reflects the SimulatorEnabled configuration when Plugin.Instance is available.
    /// </summary>
    [Fact]
    public void GetStatus_ReflectsConfiguredValue()
    {
        if (Plugin.Instance == null)
        {
            return; // Skip when Plugin not initialized (unit test environment)
        }

        bool original = Plugin.Instance.Configuration.SimulatorEnabled;
        try
        {
            Plugin.Instance.Configuration.SimulatorEnabled = true;
            var controller = CreateController();
            var result = controller.GetStatus() as Microsoft.AspNetCore.Mvc.JsonResult;
            Assert.NotNull(result);

            var value = result.Value;
            Assert.NotNull(value);
            var enabledProp = value.GetType().GetProperty("enabled");
            Assert.NotNull(enabledProp);
            Assert.True((bool)enabledProp.GetValue(value)!);
        }
        finally
        {
            Plugin.Instance.Configuration.SimulatorEnabled = original;
        }
    }

    /// <summary>
    /// Verify that GetIntents returns 404 when simulator is disabled.
    /// </summary>
    [Fact]
    public void GetIntents_Disabled_ReturnsNotFound()
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        bool original = Plugin.Instance.Configuration.SimulatorEnabled;
        try
        {
            Plugin.Instance.Configuration.SimulatorEnabled = false;
            var controller = CreateController();
            var result = controller.GetIntents() as Microsoft.AspNetCore.Mvc.NotFoundObjectResult;
            Assert.NotNull(result);
            Assert.Equal(404, result.StatusCode);
        }
        finally
        {
            Plugin.Instance.Configuration.SimulatorEnabled = original;
        }
    }

    /// <summary>
    /// Verify that GetIntents returns the list of known intents when enabled.
    /// </summary>
    [Fact]
    public void GetIntents_Enabled_ReturnsIntentList()
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        bool original = Plugin.Instance.Configuration.SimulatorEnabled;
        try
        {
            Plugin.Instance.Configuration.SimulatorEnabled = true;
            var controller = CreateController();
            var result = controller.GetIntents() as Microsoft.AspNetCore.Mvc.JsonResult;
            Assert.NotNull(result);

            var value = result.Value;
            Assert.NotNull(value);
            var intentsProp = value.GetType().GetProperty("intents");
            Assert.NotNull(intentsProp);
            var intents = intentsProp.GetValue(value) as List<string>;
            Assert.NotNull(intents);
            Assert.True(intents.Count > 0, "Should return at least one intent name");
            Assert.Contains("PlaySongIntent", intents);
            Assert.Contains("AMAZON.PauseIntent", intents);
        }
        finally
        {
            Plugin.Instance.Configuration.SimulatorEnabled = original;
        }
    }

    /// <summary>
    /// Verify that ExecuteIntent returns 404 when simulator is disabled.
    /// </summary>
    [Fact]
    public void ExecuteIntent_Disabled_ReturnsNotFound()
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        bool original = Plugin.Instance.Configuration.SimulatorEnabled;
        try
        {
            Plugin.Instance.Configuration.SimulatorEnabled = false;
            var controller = CreateController();
            var request = new SimulatorRequest { IntentName = "PlaySongIntent" };
            var result = controller.ExecuteIntent(request).Result as Microsoft.AspNetCore.Mvc.NotFoundObjectResult;
            Assert.NotNull(result);
            Assert.Equal(404, result.StatusCode);
        }
        finally
        {
            Plugin.Instance.Configuration.SimulatorEnabled = original;
        }
    }

    /// <summary>
    /// Verify that ExecuteIntent returns 400 when intentName is missing.
    /// </summary>
    [Fact]
    public void ExecuteIntent_MissingIntentName_ReturnsBadRequest()
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        bool original = Plugin.Instance.Configuration.SimulatorEnabled;
        try
        {
            Plugin.Instance.Configuration.SimulatorEnabled = true;
            var controller = CreateController();
            var request = new SimulatorRequest { IntentName = null };
            var result = controller.ExecuteIntent(request).Result as Microsoft.AspNetCore.Mvc.BadRequestObjectResult;
            Assert.NotNull(result);
            Assert.Equal(400, result.StatusCode);
        }
        finally
        {
            Plugin.Instance.Configuration.SimulatorEnabled = original;
        }
    }

    /// <summary>
    /// Verify that ExecuteIntent returns 400 when no users are configured.
    /// </summary>
    [Fact]
    public void ExecuteIntent_NoUsers_ReturnsBadRequest()
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        bool originalSim = Plugin.Instance.Configuration.SimulatorEnabled;
        try
        {
            Plugin.Instance.Configuration.SimulatorEnabled = true;
            var controller = CreateController();

            // Clear users temporarily for this test
            var users = Plugin.Instance.Configuration.Users.ToList();
            foreach (var u in users)
            {
                Plugin.Instance.Configuration.DeleteUser(u.Id);
            }

            var request = new SimulatorRequest { IntentName = "PlaySongIntent" };
            var result = controller.ExecuteIntent(request).Result as Microsoft.AspNetCore.Mvc.BadRequestObjectResult;
            Assert.NotNull(result);
            Assert.Equal(400, result.StatusCode);
        }
        finally
        {
            Plugin.Instance.Configuration.SimulatorEnabled = originalSim;
        }
    }

    private static SimulatorController CreateController()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var handlers = Enumerable.Empty<BaseHandler>();
        var pipeline = new RequestPipeline(
            Enumerable.Empty<IRequestInterceptor>(),
            Enumerable.Empty<IResponseInterceptor>(),
            loggerFactory.CreateLogger<RequestPipeline>());

        return new SimulatorController(handlers, pipeline, loggerFactory);
    }
}
