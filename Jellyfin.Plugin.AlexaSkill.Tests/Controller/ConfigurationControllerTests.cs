using System;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

public class ConfigurationControllerTests
{
    /// <summary>
    /// Regression test for JF-29: CreateNewUserSkill was ignoring the user-provided
    /// invocation name and always using Config.InvocationName ("jellyfin player") instead.
    /// </summary>
    [Fact]
    public void UserSkill_StoresProvidedInvocationName()
    {
        string customName = "my custom skill";
        var config = new PluginConfiguration();

        var userSkill = new UserSkill
        {
            InvocationName = customName,
            UserSkillStatus = UserSkillStatus.LwaAuthPending
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            UserSkill = userSkill
        };

        config.AddUser(user);

        Assert.Single(config.Users);
        Assert.Equal(customName, config.Users[0].UserSkill!.InvocationName);
        Assert.NotEqual(Config.InvocationName, config.Users[0].UserSkill!.InvocationName);
    }
}
