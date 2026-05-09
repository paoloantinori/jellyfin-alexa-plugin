using System;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Microsoft.Extensions.Logging;
using Moq;
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
        EnsurePluginInstance();
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

    private static void EnsurePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-skill-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(System.IO.Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(Configuration.PluginConfiguration), It.IsAny<string>()))
            .Returns(new Configuration.PluginConfiguration());

        var userManager = new Mock<MediaBrowser.Controller.Library.IUserManager>();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());

        new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            loggerFactory,
            userManager.Object);
    }
}
