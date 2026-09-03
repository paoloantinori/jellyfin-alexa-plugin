using System;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
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

    /// <summary>
    /// JF-348: the literal "*" in the rebuild request body must reach the redeployer as a
    /// null locale filter (all locales), while every pre-existing caller shape keeps its
    /// old meaning (specific locale rebuilds it; absent locale falls back to the saved
    /// CustomModelLocale).
    /// </summary>
    [Fact]
    public void ResolveRebuildLocaleFilter_AllSentinel_MapsToNull()
    {
        Assert.Null(ConfigurationController.ResolveRebuildLocaleFilter("*", "it-IT"));
    }

    [Fact]
    public void ResolveRebuildLocaleFilter_SpecificLocale_PassesThrough()
    {
        Assert.Equal("de-DE", ConfigurationController.ResolveRebuildLocaleFilter("de-DE", "it-IT"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRebuildLocaleFilter_AbsentLocale_UsesConfiguredFallback(string? requested)
    {
        Assert.Equal("it-IT", ConfigurationController.ResolveRebuildLocaleFilter(requested, "it-IT"));
    }

    [Fact]
    public void ResolveRebuildLocaleFilter_AbsentLocaleAndNoFallback_MapsToNull()
    {
        // Matches pre-JF-348 behavior: an empty fallback flowed into BuildSkillInteractionModels,
        // which treats a blank filter as "every locale".
        Assert.Null(ConfigurationController.ResolveRebuildLocaleFilter(null, string.Empty));
    }
}
