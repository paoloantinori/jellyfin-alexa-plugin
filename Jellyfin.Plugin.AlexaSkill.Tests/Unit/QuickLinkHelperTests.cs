using System;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class QuickLinkHelperTests
{
    private const string TestSkillId = "amzn1.ask.skill.abc-123-def";

    [Fact]
    public void GetSkillUrl_ReturnsCorrectBaseUrl()
    {
        string url = QuickLinkHelper.GetSkillUrl(TestSkillId);

        Assert.Equal("https://alexa.amazon.com/spa/index.html#skill/amzn1.ask.skill.abc-123-def", url);
    }

    [Fact]
    public void GetSkillUrl_EscapesSkillId()
    {
        string url = QuickLinkHelper.GetSkillUrl("amzn1.ask.skill.test id with spaces");

        Assert.Contains("test%20id%20with%20spaces", url);
    }

    [Fact]
    public void GetSkillUrl_NullSkillId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QuickLinkHelper.GetSkillUrl(null!));
    }

    [Fact]
    public void GetSkillUrl_EmptySkillId_Throws()
    {
        Assert.Throws<ArgumentException>(() => QuickLinkHelper.GetSkillUrl(string.Empty));
    }

    [Fact]
    public void GetTaskUrl_ReturnsCorrectUrl()
    {
        string url = QuickLinkHelper.GetTaskUrl(TestSkillId, "PlayFavorites");

        Assert.Equal(
            "https://alexa.amazon.com/spa/index.html#skill/amzn1.ask.skill.abc-123-def?task=PlayFavorites&version=1",
            url);
    }

    [Fact]
    public void GetTaskUrl_CustomVersion_ReturnsCorrectUrl()
    {
        string url = QuickLinkHelper.GetTaskUrl(TestSkillId, "PlayMedia", "2");

        Assert.Contains("version=2", url);
        Assert.Contains("task=PlayMedia", url);
    }

    [Fact]
    public void GetTaskUrl_EscapesTaskName()
    {
        string url = QuickLinkHelper.GetTaskUrl(TestSkillId, "task with spaces");

        Assert.Contains("task=task%20with%20spaces", url);
    }

    [Fact]
    public void GetTaskUrl_NullTaskName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QuickLinkHelper.GetTaskUrl(TestSkillId, null!));
    }

    [Fact]
    public void GetTaskUrl_EmptyTaskName_Throws()
    {
        Assert.Throws<ArgumentException>(() => QuickLinkHelper.GetTaskUrl(TestSkillId, string.Empty));
    }

    [Fact]
    public void GetTaskUrl_NullSkillId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QuickLinkHelper.GetTaskUrl(null!, "PlayFavorites"));
    }

    [Fact]
    public void GetPlayFavoritesUrl_ReturnsPlayFavoritesTask()
    {
        string url = QuickLinkHelper.GetPlayFavoritesUrl(TestSkillId);

        Assert.Contains("task=PlayFavorites", url);
        Assert.Contains(TestSkillId, url);
    }

    [Fact]
    public void GetPlayMediaUrl_ReturnsPlayMediaTask()
    {
        string url = QuickLinkHelper.GetPlayMediaUrl(TestSkillId);

        Assert.Contains("task=PlayMedia", url);
        Assert.Contains(TestSkillId, url);
    }
}
