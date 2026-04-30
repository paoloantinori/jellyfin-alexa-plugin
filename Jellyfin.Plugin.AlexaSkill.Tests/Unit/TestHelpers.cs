using System;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

internal static class TestHelpers
{
    internal static User CreateTestUser(Guid? id = null, string invocationName = "test", string jellyfinToken = "test-token")
    {
        return new User { Id = id ?? Guid.NewGuid(), InvocationName = invocationName, JellyfinToken = jellyfinToken };
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
        var field = typeof(PluginConfiguration).GetField("serverAddress", BindingFlags.NonPublic | BindingFlags.Instance);
        field?.SetValue(config, address);
    }
}
