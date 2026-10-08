using System;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class UserTests : PluginTestBase
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

    /// <summary>
    /// JF-823: the fourth stored catalog id rides the on-disk XmlSerializer
    /// configuration round trip exactly like its three siblings. A plain
    /// string? property is XmlSerializer-safe by shape (the Dictionary trap
    /// the repo rule bans would crash the whole config save, not just this
    /// field); this pin holds the round trip itself, so a future DTO change
    /// that breaks the serialization of the stored ids fails here.
    /// </summary>
    [Fact]
    public void StoredCatalogIds_IncludingAudiobook_RoundTripThroughXmlSerialization()
    {
        var config = new Configuration.PluginConfiguration();
        config.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            ArtistCatalogId = "cat-artist",
            AlbumCatalogId = "cat-album",
            SeriesCatalogId = "cat-series",
            AudiobookCatalogId = "cat-audiobook"
        });

        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Configuration.PluginConfiguration));
        using var ms = new System.IO.MemoryStream();
        serializer.Serialize(ms, config);
        ms.Position = 0;
        var deserialized = (Configuration.PluginConfiguration)serializer.Deserialize(ms)!;

        User roundTripped = Assert.Single(deserialized.Users);
        Assert.Equal("cat-artist", roundTripped.ArtistCatalogId);
        Assert.Equal("cat-album", roundTripped.AlbumCatalogId);
        Assert.Equal("cat-series", roundTripped.SeriesCatalogId);
        Assert.Equal("cat-audiobook", roundTripped.AudiobookCatalogId);
    }

    private static void EnsurePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            return;
        }

        var tmpDir = TestHelpers.CreateRegisteredTempDir("alexa-skill-test");

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
