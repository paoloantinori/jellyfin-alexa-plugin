using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for JF-300: invocation-name locale defaults.
/// Empty/null UserSkill.InvocationName means "use locale defaults"
/// (it-IT → "mia collezione", all other locales → "jellyfin player").
/// A non-empty custom name applies to ALL 17 locales, including it-IT.
/// </summary>
[Collection("Plugin")]
public class InvocationNameDefaultsTests : PluginTestBase
{
    private const string ItIt = "mia collezione";
    private const string DefaultName = "jellyfin player";

    // ---------- Config.EffectiveInvocationName (pure resolution logic) ----------

    /// <summary>
    /// JF-558: the JF-558 locale-default table (native names where the English
    /// invocation proved unreliable) resolves, explicit user names still win over
    /// every default, and en-* falls through to the global "jellyfin player".
    /// </summary>
    [Fact]
    public void JF558_LocaleDefaults_ResolveAndExplicitStillWins()
    {
        // the 11 new locale defaults resolve
        Assert.Equal("meine sammlung", Config.EffectiveInvocationName("de-DE", string.Empty));
        Assert.Equal("mi colección", Config.EffectiveInvocationName("es-ES", string.Empty));
        Assert.Equal("mon serveur", Config.EffectiveInvocationName("fr-FR", string.Empty));
        Assert.Equal("mon serveur", Config.EffectiveInvocationName("fr-CA", string.Empty));
        Assert.Equal("minha coleção", Config.EffectiveInvocationName("pt-BR", string.Empty));
        Assert.Equal("mijn collectie", Config.EffectiveInvocationName("nl-NL", string.Empty));
        // Paolo-confirmed 2026-09-21: hi takes the naturalized loanword, ar the short form
        Assert.Equal("मेरी कलेक्शन", Config.EffectiveInvocationName("hi-IN", string.Empty));
        Assert.Equal("مجموعتي", Config.EffectiveInvocationName("ar-SA", string.Empty));
        Assert.Equal("マイコレクション", Config.EffectiveInvocationName("ja-JP", string.Empty));

        // an explicit user name STILL overrides every locale default (the
        // requirement: users with an explicit invocation are untouched)
        Assert.Equal("il mio nome", Config.EffectiveInvocationName("de-DE", "il mio nome"));
        Assert.Equal("mon serveur perso", Config.EffectiveInvocationName("fr-FR", "mon serveur perso"));

        // en-* falls through to the global default (unchanged)
        Assert.Equal(Config.InvocationName, Config.EffectiveInvocationName("en-US", string.Empty));
    }

    /// <summary>
    /// JF-558 live lesson: Amazon rejects any uppercase letter in an invocation
    /// name (de-DE "meine Sammlung" build FAILED with InvalidCharInInvocationName;
    /// the lowercase retry built). Every locale default must be all-lowercase.
    /// </summary>
    [Fact]
    public void JF558_LocaleDefaults_AreAllLowercase()
    {
        foreach (var kvp in Config.LocaleInvocationNames)
        {
            Assert.False(kvp.Value.Any(char.IsUpper), $"locale {kvp.Key} name '{kvp.Value}' must be all lowercase (Amazon InvalidCharInInvocationName)");
            Assert.Equal(kvp.Value.Trim(), kvp.Value);
        }
    }

    [Fact]
    public void EffectiveInvocationName_Empty_FallsBackToLocaleDefault()
    {
        Assert.Equal(ItIt, Config.EffectiveInvocationName("it-IT", string.Empty));
    }

    [Fact]
    public void EffectiveInvocationName_Null_FallsBackToLocaleDefault()
    {
        Assert.Equal(ItIt, Config.EffectiveInvocationName("it-IT", null));
    }

    [Fact]
    public void EffectiveInvocationName_WhiteSpace_FallsBackToLocaleDefault()
    {
        Assert.Equal(ItIt, Config.EffectiveInvocationName("it-IT", "   "));
    }

    [Fact]
    public void EffectiveInvocationName_Empty_FallsBackToGlobalDefault_ForOtherLocales()
    {
        Assert.Equal(DefaultName, Config.EffectiveInvocationName("en-US", string.Empty));
        Assert.Equal(DefaultName, Config.EffectiveInvocationName("en-GB", null));

        // JF-558: de-DE and fr-FR now carry locale defaults; only whitespace
        // (an effectively-empty stored name) still resolves them to their locale
        // entry, which is the intended "no explicit name" behavior.
        Assert.Equal("meine sammlung", Config.EffectiveInvocationName("de-DE", null));
        Assert.Equal("mon serveur", Config.EffectiveInvocationName("fr-FR", "  "));
    }

    [Fact]
    public void EffectiveInvocationName_CustomName_AppliesToItIt()
    {
        Assert.Equal("pinco pallino", Config.EffectiveInvocationName("it-IT", "pinco pallino"));
    }

    [Fact]
    public void EffectiveInvocationName_CustomName_AppliesToAllLocales()
    {
        Assert.Equal("my skill", Config.EffectiveInvocationName("it-IT", "my skill"));
        Assert.Equal("my skill", Config.EffectiveInvocationName("en-US", "my skill"));
        Assert.Equal("my skill", Config.EffectiveInvocationName("ja-JP", "my skill"));
    }

    // ---------- BuildSkillInteractionModels (end-to-end) ----------

    [Fact]
    public void BuildSkillInteractionModels_EmptyName_ItItGetsLocaleDefault()
    {
        var plugin = EnsurePluginInstance();
        var models = plugin.BuildSkillInteractionModels(string.Empty);

        var itIt = models.FirstOrDefault(m => m.Locale == "it-IT");
        Assert.NotNull(itIt);
        Assert.Equal(ItIt, itIt!.InvocationName);
    }

    [Fact]
    public void BuildSkillInteractionModels_EmptyName_OtherLocalesGetGlobalDefault()
    {
        var plugin = EnsurePluginInstance();
        var models = plugin.BuildSkillInteractionModels(string.Empty);

        var enUs = models.FirstOrDefault(m => m.Locale == "en-US");
        Assert.NotNull(enUs);
        Assert.Equal(DefaultName, enUs!.InvocationName);
    }

    [Fact]
    public void BuildSkillInteractionModels_CustomName_AppliesToAllLocalesIncludingItIt()
    {
        // Regression for the original bug: a custom name must reach it-IT too.
        const string custom = "pinco pallino blu";
        var plugin = EnsurePluginInstance();
        var models = plugin.BuildSkillInteractionModels(custom);

        // Every locale, including it-IT, must carry the custom name.
        Assert.All(models, m => Assert.Equal(custom, m.InvocationName));
        Assert.Contains(models, m => m.Locale == "it-IT");
    }

    [Fact]
    public void BuildSkillInteractionModels_NullName_UsesLocaleDefaults()
    {
        var plugin = EnsurePluginInstance();
        var models = plugin.BuildSkillInteractionModels(null!);

        Assert.Equal(ItIt, models.First(m => m.Locale == "it-IT").InvocationName);
        Assert.Equal(DefaultName, models.First(m => m.Locale == "en-US").InvocationName);
    }

    // ---------- Migration: stored "jellyfin player" → treated as default ----------

    [Fact]
    public void Migration_UserWithStoredDefault_ClearsInvocationName()
    {
        // Pre-JF-300 user whose stored InvocationName is the global default.
        var config = new PluginConfiguration();
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserSkill = new UserSkill { InvocationName = DefaultName }
        };
        config.Users.Add(user);

        // Call the migration directly (it's internal; InternalsVisibleTo is set).
        Plugin.MigrateDefaultInvocationNames(config);

        Assert.Equal(string.Empty, user.UserSkill!.InvocationName);
    }

    [Fact]
    public void Migration_UserWithCustomName_Preserved()
    {
        const string custom = "my custom skill";
        var config = new PluginConfiguration();
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserSkill = new UserSkill { InvocationName = custom }
        };
        config.Users.Add(user);

        Plugin.MigrateDefaultInvocationNames(config);

        Assert.Equal(custom, user.UserSkill!.InvocationName);
    }

    [Fact]
    public void Migration_StoredDefault_ThenBuild_ItItStaysLocaleDefault()
    {
        // Full regression: a legacy user with stored "jellyfin player" must NOT
        // cause it-IT to become "jellyfin player". After migration their effective
        // invocation name is empty, so it-IT stays "mia collezione".
        Assert.Equal(ItIt, Config.EffectiveInvocationName("it-IT", string.Empty));
        Assert.Equal(ItIt, Config.EffectiveInvocationName("it-IT", null));

        // And the literal default, if NOT migrated, WOULD clobber it-IT — which is
        // exactly the regression the migration prevents.
        Assert.Equal(DefaultName, Config.EffectiveInvocationName("it-IT", DefaultName));
    }

    /// <summary>
    /// Constructs a Plugin instance with mocked Jellyfin services. Optionally
    /// injects a pre-populated configuration that BasePlugin will deserialize.
    /// </summary>
    private static Plugin EnsurePluginInstance(PluginConfiguration? configToLoad = null)
    {
        if (Plugin.Instance != null)
        {
            return Plugin.Instance;
        }

        string tmpDir = TestHelpers.CreateRegisteredTempDir("jf300");

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
            .Returns(configToLoad ?? new PluginConfiguration());

        var userManager = new Mock<MediaBrowser.Controller.Library.IUserManager>();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());

        return new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            loggerFactory,
            userManager.Object);
    }
}
