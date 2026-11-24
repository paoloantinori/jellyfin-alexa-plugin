#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-960 wiring: <see cref="Plugin.BuildSkillInteractionModels"/> must prune the
/// locale models ONLY when <see cref="PluginConfiguration.MediaProfiledModels"/> is on
/// (the default OFF keeps the output byte-identical to the embedded models, so the
/// household's NLU suite keeps measuring the full model), and must fail open when the
/// library manager is unavailable.
/// </summary>
[Collection("Plugin")]
public class MediaProfiledModelsBuildWiringTests : PluginTestBase
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();

    /// <summary>
    /// The music-only library mock: one row only for Audio-kind queries, nothing for
    /// every other family (and nothing at all while the flag-off tests run, since the
    /// census must never be reached with the flag off).
    /// </summary>
    private void SetupMusicOnlyLibrary()
    {
        _libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
                Array.Exists(q.IncludeItemTypes ?? Array.Empty<BaseItemKind>(), k => k == BaseItemKind.Audio)
                    ? new List<BaseItem> { new AudioItem { Id = Guid.NewGuid(), Name = "song" } }
                    : new List<BaseItem>());
    }

    private Plugin CreatePlugin(bool mediaProfiledModels, int linkedUsers = 1)
    {
        var config = new PluginConfiguration { MediaProfiledModels = mediaProfiledModels };
        for (int i = 0; i < linkedUsers; i++)
        {
            var pluginUser = new User { Id = Guid.NewGuid() };
            config.Users.Add(pluginUser);
            _userManager
                .Setup(u => u.GetUserById(pluginUser.Id))
                .Returns(TestHelpers.CreateJellyfinUser(id: pluginUser.Id));
        }

        string tmpDir = TestHelpers.CreateRegisteredTempDir("jf960");

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
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            LoggerFactory.Create(b => { }),
            _userManager.Object);

        plugin.LibraryManager = _libraryManager.Object;
        return plugin;
    }

    private static string Serialize(SkillInteraction interaction) => JsonConvert.SerializeObject(interaction);

    private static SkillInteraction BuildEmbeddedModel(string locale, string invocationName)
    {
        return new SkillInteractionModel(locale, TestLocales.ResourcePath(locale), invocationName).InteractionModel;
    }

    /// <summary>
    /// The flag OFF (the default) must change NOTHING: the built model serializes
    /// byte-identical to the embedded resource model for the same invocation name,
    /// and the library manager is never queried.
    /// </summary>
    [Fact]
    public void Build_FlagOff_Default_IsByteIdenticalToEmbedded()
    {
        Plugin plugin = CreatePlugin(mediaProfiledModels: false);
        var models = plugin.BuildSkillInteractionModels("test skill");

        var it = models.First(m => m.Locale == "it-IT");
        string expected = Serialize(BuildEmbeddedModel("it-IT", it.InvocationName));
        Assert.Equal(expected, Serialize(it.InteractionModel));

        // The flag-off path must not even reach the library manager (no census, no
        // pruning), whatever the library mock would have answered.
        _libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void Build_FlagOn_MusicOnlyCensus_PrunesAbsentFamilies_EveryLocale()
    {
        SetupMusicOnlyLibrary();
        Plugin plugin = CreatePlugin(mediaProfiledModels: true);

        var models = plugin.BuildSkillInteractionModels("test skill");

        Assert.NotEmpty(models);
        Assert.All(models, model =>
        {
            IEnumerable<string> names = model.InteractionModel.Language.IntentTypes!.Select(i => i.Name);
            Assert.Contains("PlaySongIntent", names);
            Assert.Contains("FindSongIntent", names);
            Assert.Contains("AMAZON.StopIntent", names);
            Assert.Contains("SearchMediaIntent", names);
            Assert.DoesNotContain("PlayVideoIntent", names);
            Assert.DoesNotContain("PlayEpisodeIntent", names);
            Assert.DoesNotContain("PlayBookIntent", names);
            Assert.DoesNotContain("PlayPodcastIntent", names);
            Assert.DoesNotContain("PlayChannelIntent", names);
        });

        // Dialog consistency: the pruned intents' dialog entries dropped too.
        Assert.All(models, model =>
        {
            IEnumerable<string>? dialogNames = model.InteractionModel.Dialog?.Intents?.Select(i => i.Name);
            if (dialogNames != null)
            {
                Assert.DoesNotContain("PlayEpisodeIntent", dialogNames);
            }
        });
    }

    /// <summary>
    /// The default-off invariant (review round): a FRESH configuration carries the
    /// flag FALSE, so an accidental default flip cannot ship pruned models to real
    /// skills with the suite green.
    /// </summary>
    [Fact]
    public void MediaProfiledModels_FreshConfig_DefaultsOff()
    {
        Assert.False(new PluginConfiguration().MediaProfiledModels);
    }

    /// <summary>
    /// The census is ONE per-instance union but the builder runs once PER USER: with
    /// the flag on and three linked users, repeated batch builds must reuse the
    /// cached census (zero additional library queries after the first batch).
    /// </summary>
    [Fact]
    public void Build_FlagOn_RepeatedBatches_CensusComputedOncePerUsersSnapshot()
    {
        int getItemsListCalls = 0;
        _libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback(() => getItemsListCalls++)
            .Returns((InternalItemsQuery q) =>
                Array.Exists(q.IncludeItemTypes ?? Array.Empty<BaseItemKind>(), k => k == BaseItemKind.Audio)
                    ? new List<BaseItem> { new AudioItem { Id = Guid.NewGuid(), Name = "song" } }
                    : new List<BaseItem>());

        Plugin plugin = CreatePlugin(mediaProfiledModels: true, linkedUsers: 3);

        plugin.BuildSkillInteractionModels("batch one");
        int afterFirstBatch = getItemsListCalls;
        Assert.True(afterFirstBatch > 0, "the first batch must actually run the census");

        // The per-user startup loop shape: two more users build models against the
        // same linked-users snapshot; the cached census answers them without a single
        // additional library query.
        plugin.BuildSkillInteractionModels("batch two");
        plugin.BuildSkillInteractionModels("batch three");
        Assert.Equal(afterFirstBatch, getItemsListCalls);
    }

    /// <summary>
    /// Fail open at the wiring layer: the flag on but no library manager (a startup
    /// that never ran) keeps the full model.
    /// </summary>
    [Fact]
    public void Build_FlagOn_WithoutLibraryManager_KeepsFullModel()
    {
        Plugin plugin = CreatePlugin(mediaProfiledModels: true);
        plugin.LibraryManager = null;

        var models = plugin.BuildSkillInteractionModels("test skill");

        var it = models.First(m => m.Locale == "it-IT");
        string expected = Serialize(BuildEmbeddedModel("it-IT", it.InvocationName));
        Assert.Equal(expected, Serialize(it.InteractionModel));
    }
}
