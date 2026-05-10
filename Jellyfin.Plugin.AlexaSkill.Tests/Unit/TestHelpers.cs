using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

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
}
