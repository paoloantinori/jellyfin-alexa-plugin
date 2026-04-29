using System;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PluginConfigurationTests
{
    private PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration();
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var config = CreateConfig();
        Assert.NotNull(config);
    }

    [Fact]
    public void Constructor_InitializesEmptyLwaClientId()
    {
        var config = CreateConfig();
        Assert.Equal(string.Empty, config.LwaClientId);
    }

    [Fact]
    public void Constructor_InitializesEmptyLwaClientSecret()
    {
        var config = CreateConfig();
        Assert.Equal(string.Empty, config.LwaClientSecret);
    }

    [Fact]
    public void Constructor_InitializesAccountLinkingClientId()
    {
        var config = CreateConfig();
        Assert.NotEqual(Guid.Empty.ToString(), config.AccountLinkingClientId);
    }

    [Fact]
    public void Constructor_InitializesEmptyUsersList()
    {
        var config = CreateConfig();
        Assert.Empty(config.Users);
    }

    [Fact]
    public void AddUser_AddsUserToList()
    {
        var config = CreateConfig();
        var user = TestHelpers.CreateTestUser();

        config.AddUser(user);

        Assert.Single(config.Users);
        Assert.Equal(user.Id, config.Users[0].Id);
    }

    [Fact]
    public void AddUser_DuplicateUser_ThrowsArgumentException()
    {
        var config = CreateConfig();
        var guid = Guid.NewGuid();
        config.AddUser(TestHelpers.CreateTestUser(guid, "test1"));

        Assert.Throws<ArgumentException>(() => config.AddUser(TestHelpers.CreateTestUser(guid, "test2")));
    }

    [Fact]
    public void GetUserById_ReturnsUser_WhenExists()
    {
        var config = CreateConfig();
        var guid = Guid.NewGuid();
        config.AddUser(TestHelpers.CreateTestUser(guid));

        var result = config.GetUserById(guid);

        Assert.NotNull(result);
        Assert.Equal(guid, result!.Id);
    }

    [Fact]
    public void GetUserById_ReturnsNull_WhenNotFound()
    {
        var config = CreateConfig();

        Assert.Null(config.GetUserById(Guid.NewGuid()));
    }

    [Fact]
    public void DeleteUser_RemovesUser_WhenExists()
    {
        var config = CreateConfig();
        var guid = Guid.NewGuid();
        config.AddUser(TestHelpers.CreateTestUser(guid));

        var result = config.DeleteUser(guid);

        Assert.True(result);
        Assert.Empty(config.Users);
    }

    [Fact]
    public void DeleteUser_ReturnsFalse_WhenNotFound()
    {
        var config = CreateConfig();

        Assert.False(config.DeleteUser(Guid.NewGuid()));
    }
}
