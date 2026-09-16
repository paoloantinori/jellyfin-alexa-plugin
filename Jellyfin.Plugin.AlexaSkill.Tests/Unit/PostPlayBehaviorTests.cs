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
/// Note (JF-315 batch 10): the GetPostPlayBehavior RESOLUTION tests that lived here
/// as a logic mirror ("Mirrors BaseHandler.GetPostPlayBehavior logic") were replaced
/// by direct facts in ProgressReporterTests when the member moved to
/// ProgressReporter.GetPostPlayBehavior; the mirror re-implemented the resolution
/// instead of exercising it, so the direct facts subsume all four shapes.
/// </summary>
