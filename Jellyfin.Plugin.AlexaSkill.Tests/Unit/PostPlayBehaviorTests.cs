using Jellyfin.Plugin.AlexaSkill.Configuration;
using Xunit;
using User = Jellyfin.Plugin.AlexaSkill.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for PostPlayBehavior enum defaults.
/// </summary>
public class PostPlayBehaviorEnumTests
{
    [Fact]
    public void Default_Is_Stop()
    {
        Assert.Equal(PostPlayBehavior.Stop, default(PostPlayBehavior));
    }

    [Fact]
    public void Values_AreOrdered()
    {
        Assert.Equal(0, (int)PostPlayBehavior.Stop);
        Assert.Equal(1, (int)PostPlayBehavior.AutoPlay);
    }
}

/// <summary>
/// Tests for PluginConfiguration PostPlayBehavior defaults.
/// </summary>
public class PostPlayConfigDefaultsTests
{
    [Fact]
    public void DefaultPostPlayBehavior_DefaultsToStop()
    {
        var config = new PluginConfiguration();
        Assert.Equal(PostPlayBehavior.Stop, config.DefaultPostPlayBehavior);
    }

    [Fact]
    public void DefaultPostPlayBehavior_CanBeSet()
    {
        var config = new PluginConfiguration { DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay };
        Assert.Equal(PostPlayBehavior.AutoPlay, config.DefaultPostPlayBehavior);
    }
}

/// <summary>
/// Tests for User per-user PostPlayBehavior override.
/// </summary>
public class PostPlayUserOverrideTests
{
    [Fact]
    public void User_PostPlayBehavior_DefaultsToNull()
    {
        var user = new User();
        Assert.Null(user.PostPlayBehavior);
    }

    [Fact]
    public void User_PostPlayBehavior_CanBeSet()
    {
        var user = new User { PostPlayBehavior = PostPlayBehavior.AutoPlay };
        Assert.Equal(PostPlayBehavior.AutoPlay, user.PostPlayBehavior);
    }

    [Fact]
    public void User_PostPlayBehavior_CanBeCleared()
    {
        var user = new User { PostPlayBehavior = PostPlayBehavior.AutoPlay };
        user.PostPlayBehavior = null;
        Assert.Null(user.PostPlayBehavior);
    }
}

/// <summary>
/// Tests for GetPostPlayBehavior resolution logic: user override → global default.
/// The resolution is tested inline to avoid Alexa.NET namespace shadowing from the
/// test project's Alexa/ directory, which prevents creating a BaseHandler subclass.
/// </summary>
public class GetPostPlayBehaviorResolutionTests
{
    [Fact]
    public void NullUser_ReturnsGlobalDefault()
    {
        var config = new PluginConfiguration { DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay };
        Assert.Equal(PostPlayBehavior.AutoPlay, Resolve(config, null));
    }

    [Fact]
    public void NullOverride_ReturnsGlobalDefault()
    {
        var config = new PluginConfiguration { DefaultPostPlayBehavior = PostPlayBehavior.AutoPlay };
        var user = new User { PostPlayBehavior = null };
        Assert.Equal(PostPlayBehavior.AutoPlay, Resolve(config, user));
    }

    [Fact]
    public void UserOverride_WinsOverGlobal()
    {
        var config = new PluginConfiguration { DefaultPostPlayBehavior = PostPlayBehavior.Stop };
        var user = new User { PostPlayBehavior = PostPlayBehavior.AutoPlay };
        Assert.Equal(PostPlayBehavior.AutoPlay, Resolve(config, user));
    }

    [Fact]
    public void GlobalDefault_WhenNoOverrideAndNoConfig()
    {
        var config = new PluginConfiguration();
        Assert.Equal(PostPlayBehavior.Stop, Resolve(config, null));
    }

    /// <summary>
    /// Mirrors BaseHandler.GetPostPlayBehavior logic: user override → global default.
    /// </summary>
    private static PostPlayBehavior Resolve(PluginConfiguration config, User? user)
    {
        if (user?.PostPlayBehavior is { } userBehavior)
        {
            return userBehavior;
        }

        return config.DefaultPostPlayBehavior;
    }
}
