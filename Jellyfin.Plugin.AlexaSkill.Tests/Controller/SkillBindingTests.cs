#nullable enable

using System;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// The skill-binding gate: a request is served only when its signed
/// envelope's ApplicationId is the resolved user's own skill id. Signature
/// verification alone proves Alexa-origin, not skill identity, so the gate is
/// what stops a foreign skill from reaching a public endpoint with a known
/// user id. Fail closed on every missing piece.
/// </summary>
public class SkillBindingTests
{
    private static Entities.User UserWithSkill(string skillId) => new()
    {
        Id = Guid.NewGuid(),
        InvocationName = "test",
        JellyfinToken = "test-token",
        UserSkill = new UserSkill { SkillId = skillId }
    };

    [Fact]
    public void MatchingApplicationId_Binds()
    {
        Assert.True(AlexaSkillController.IsBoundToUserSkill(
            "amzn1.ask.skill.11111111-1111-1111-1111-111111111111",
            UserWithSkill("amzn1.ask.skill.11111111-1111-1111-1111-111111111111")));
    }

    [Fact]
    public void ForeignSkillApplicationId_NeverBinds()
    {
        // The attacker's own skill carries its own id in the signed envelope:
        // a real Amazon-certified request that still must be refused.
        Assert.False(AlexaSkillController.IsBoundToUserSkill(
            "amzn1.ask.skill.22222222-2222-2222-2222-222222222222",
            UserWithSkill("amzn1.ask.skill.11111111-1111-1111-1111-111111111111")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingApplicationId_FailsClosed(string? applicationId)
    {
        Assert.False(AlexaSkillController.IsBoundToUserSkill(applicationId, UserWithSkill("amzn1.ask.skill.1")));
    }

    [Fact]
    public void UnprovisionedUserSkill_FailsClosed()
    {
        var user = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test" };
        Assert.False(AlexaSkillController.IsBoundToUserSkill("amzn1.ask.skill.1", user));

        var emptySkill = UserWithSkill(string.Empty);
        Assert.False(AlexaSkillController.IsBoundToUserSkill("amzn1.ask.skill.1", emptySkill));
    }

    [Fact]
    public void BindingIsCaseSensitive_OrdinalComparison()
    {
        // Skill ids are opaque Amazon identifiers; a case difference is a
        // different id, not a formatting variant.
        Assert.False(AlexaSkillController.IsBoundToUserSkill(
            "AMZN1.ask.skill.abc",
            UserWithSkill("amzn1.ask.skill.abc")));
    }
}
