using Jellyfin.Plugin.AlexaSkill.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class UserTests
{
    [Fact]
    public void SmapiDeviceToken_SetAndGet()
    {
        var user = TestHelpers.CreateTestUser();
        var token = TestHelpers.CreateTestDeviceToken();
        user.SmapiDeviceToken = token;

        Assert.NotNull(user.SmapiDeviceToken);
        Assert.Equal("access", user.SmapiDeviceToken!.AccessToken);
    }

    [Fact]
    public void SmapiManagement_ReturnsNull_WhenNoDeviceToken()
    {
        var user = TestHelpers.CreateTestUser();

        Assert.Null(user.SmapiManagement);
    }

    [Fact]
    public void SmapiManagement_ReturnsInstance_WhenDeviceTokenSet()
    {
        var user = TestHelpers.CreateTestUser();
        user.SmapiDeviceToken = TestHelpers.CreateTestDeviceToken();

        Assert.NotNull(user.SmapiManagement);
    }

    [Fact]
    public void UserSkill_SetAndGet()
    {
        var user = TestHelpers.CreateTestUser();
        var skill = new UserSkill { SkillId = "amzn1.ask.skill.test", InvocationName = "test" };
        user.UserSkill = skill;

        Assert.NotNull(user.UserSkill);
        Assert.Equal("amzn1.ask.skill.test", user.UserSkill!.SkillId);
    }

    [Fact]
    public void Username_ReturnsEmpty_WhenIdIsEmpty()
    {
        var user = new User { Id = Guid.Empty, InvocationName = "test" };

        Assert.Equal(string.Empty, user.Username);
    }
}
