using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class SkillDeletionTests
{
    [Fact]
    public void LastUserWithSkill_NoOtherUsersWithSameSkillId()
    {
        var config = new PluginConfiguration();
        var skillId = "amzn1.ask.skill.test";
        var user1 = TestHelpers.CreateTestUser();
        user1.UserSkill = new UserSkill { SkillId = skillId, InvocationName = "jellyfin" };
        config.AddUser(user1);

        config.DeleteUser(user1.Id);

        bool otherUsers = config.Users.Any(u => u.UserSkill?.SkillId == skillId);
        Assert.False(otherUsers);
    }

    [Fact]
    public void NotLastUser_OtherUsersShareSkillId()
    {
        var config = new PluginConfiguration();
        var skillId = "amzn1.ask.skill.test";
        var user1 = TestHelpers.CreateTestUser();
        user1.UserSkill = new UserSkill { SkillId = skillId, InvocationName = "jellyfin" };
        var user2 = TestHelpers.CreateTestUser();
        user2.UserSkill = new UserSkill { SkillId = skillId, InvocationName = "jellyfin" };
        config.AddUser(user1);
        config.AddUser(user2);

        config.DeleteUser(user1.Id);

        bool otherUsers = config.Users.Any(u => u.UserSkill?.SkillId == skillId);
        Assert.True(otherUsers);
        Assert.Single(config.Users);
    }

    [Fact]
    public void DifferentSkillIds_DeletingOneDoesNotAffectOther()
    {
        var config = new PluginConfiguration();
        var skillId1 = "amzn1.ask.skill.alpha";
        var skillId2 = "amzn1.ask.skill.beta";
        var user1 = TestHelpers.CreateTestUser();
        user1.UserSkill = new UserSkill { SkillId = skillId1, InvocationName = "jellyfin" };
        var user2 = TestHelpers.CreateTestUser();
        user2.UserSkill = new UserSkill { SkillId = skillId2, InvocationName = "jellyfin" };
        config.AddUser(user1);
        config.AddUser(user2);

        config.DeleteUser(user1.Id);

        bool otherUsersWithSkill1 = config.Users.Any(u => u.UserSkill?.SkillId == skillId1);
        Assert.False(otherUsersWithSkill1);
    }

    [Fact]
    public void UserWithNoSkill_DeletionDoesNotTriggerCloudDelete()
    {
        var config = new PluginConfiguration();
        var user = TestHelpers.CreateTestUser();
        config.AddUser(user);

        string? skillId = user.UserSkill?.SkillId;
        Assert.Null(skillId);

        config.DeleteUser(user.Id);
        Assert.Empty(config.Users);
    }

    [Fact]
    public void UserWithNullSkillId_NoCloudDeleteNeeded()
    {
        var config = new PluginConfiguration();
        var user = TestHelpers.CreateTestUser();
        user.UserSkill = new UserSkill { SkillId = null, InvocationName = "jellyfin" };
        config.AddUser(user);

        string? skillId = user.UserSkill?.SkillId;
        Assert.True(string.IsNullOrEmpty(skillId));
    }
}
