using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

internal static class TestHelpers
{
    internal static Entities.User CreateTestUser(Guid? id = null, string invocationName = "test", string jellyfinToken = "test-token")
    {
        return new Entities.User { Id = id ?? Guid.NewGuid(), InvocationName = invocationName, JellyfinToken = jellyfinToken };
    }

    internal static DeviceToken CreateTestDeviceToken(
        string accessToken = "access",
        string refreshToken = "refresh",
        string tokenType = "Bearer",
        long expireTimestamp = 12345)
    {
        return new DeviceToken(accessToken, refreshToken, tokenType, expireTimestamp);
    }

    internal static void SetServerAddress(PluginConfiguration config, string address)
    {
        config.ServerAddress = address;
    }

    internal static SessionInfo CreateTestSession(ISessionManager sessionManager, ILoggerFactory loggerFactory)
    {
        return new SessionInfo(sessionManager, loggerFactory.CreateLogger<SessionInfo>());
    }

    internal static Context CreateTestContext()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
    }

    internal static Context CreateContextWithApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>
                    {
                        { "Alexa.Presentation.APL", new { } }
                    }
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    internal static Context CreateContextWithoutApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    /// <summary>
    /// Extract speech text from a SkillResponse, handling both plain text and SSML output.
    /// Strips SSML markup for content assertions.
    /// </summary>
    internal static string GetSpeechText(SkillResponse response)
    {
        if (response.Response.OutputSpeech is SsmlOutputSpeech ssml)
        {
            string raw = ssml.Ssml;
            raw = raw.Replace("<speak>", string.Empty).Replace("</speak>", string.Empty);
            raw = Regex.Replace(raw, "<break[^>]*>", " ");
            raw = Regex.Replace(raw, "<emphasis[^>]*>", string.Empty);
            raw = raw.Replace("</emphasis>", string.Empty);
            raw = Regex.Replace(raw, "<say-as[^>]*>", string.Empty);
            raw = raw.Replace("</say-as>", string.Empty);
            raw = Regex.Replace(raw, "<prosody[^>]*>", string.Empty);
            raw = raw.Replace("</prosody>", string.Empty);
            raw = Regex.Replace(raw, @"\s+", " ").Trim();
            return raw;
        }

        var speech = global::Xunit.Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        return speech.Text;
    }

    /// <summary>
    /// Asserts that the response keeps the session open (ShouldEndSession is explicitly false).
    /// This is more precise than checking null || false — ResponseBuilder.Ask() always sets false.
    /// </summary>
    internal static void AssertSessionOpen(SkillResponse response, string message = "Session should remain open")
    {
        global::Xunit.Assert.NotNull(response);
        global::Xunit.Assert.False(response.Response.ShouldEndSession ?? true, message);
    }

    /// <summary>
    /// Sets Plugin.Instance with the provided configuration so IfFeatureDisabled
    /// can read from Plugin.Instance.Configuration. When the instance already exists,
    /// only the specific flag is synced via <paramref name="syncFlag"/>.
    /// </summary>
    internal static void EnsurePluginInstance(
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        Action<PluginConfiguration> syncFlag,
        string tempDirSuffix)
    {
        if (Plugin.Instance != null)
        {
            syncFlag(Plugin.Instance.Configuration);
            return;
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), tempDirSuffix + "-" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var userManager = new Mock<IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }
}
