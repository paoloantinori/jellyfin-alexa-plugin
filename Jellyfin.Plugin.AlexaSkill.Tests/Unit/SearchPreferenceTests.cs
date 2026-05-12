using System;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for search preference defaults in PluginConfiguration.
/// </summary>
public class SearchPreferenceDefaultsTests
{
    [Fact]
    public void SearchPreferences_HaveCorrectDefaults()
    {
        var config = new PluginConfiguration();

        Assert.Equal(20, config.MaxSearchResults);
        Assert.Equal(5, config.MaxBrowseResults);
        Assert.Equal(10, config.MaxRecentlyAddedResults);
        Assert.Equal(10, config.MaxRecommendationResults);
        Assert.Equal(60, config.FuzzyMatchThreshold);
        Assert.Equal(40, config.FuzzySuggestionThreshold);
    }

    [Fact]
    public void SearchPreferences_CanBeCustomized()
    {
        var config = new PluginConfiguration
        {
            MaxSearchResults = 50,
            MaxBrowseResults = 15,
            MaxRecentlyAddedResults = 30,
            MaxRecommendationResults = 5,
            FuzzyMatchThreshold = 80,
            FuzzySuggestionThreshold = 20
        };

        Assert.Equal(50, config.MaxSearchResults);
        Assert.Equal(15, config.MaxBrowseResults);
        Assert.Equal(30, config.MaxRecentlyAddedResults);
        Assert.Equal(5, config.MaxRecommendationResults);
        Assert.Equal(80, config.FuzzyMatchThreshold);
        Assert.Equal(20, config.FuzzySuggestionThreshold);
    }

    [Fact]
    public void Validate_AcceptsValidSearchPreferences()
    {
        var config = new PluginConfiguration
        {
            MaxSearchResults = 1,
            MaxBrowseResults = 1,
            MaxRecentlyAddedResults = 1,
            MaxRecommendationResults = 1,
            FuzzyMatchThreshold = 0,
            FuzzySuggestionThreshold = 0
        };

        var errors = config.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("Search Results", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Browse Results", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Recently Added", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Recommendation Results", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Match Threshold", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Suggestion Threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsZeroMaxSearchResults()
    {
        var config = new PluginConfiguration { MaxSearchResults = 0 };
        Assert.Contains(config.Validate(), e => e.Contains("Max Search Results", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsOversizedMaxSearchResults()
    {
        var config = new PluginConfiguration { MaxSearchResults = 51 };
        Assert.Contains(config.Validate(), e => e.Contains("Max Search Results", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsZeroMaxBrowseResults()
    {
        var config = new PluginConfiguration { MaxBrowseResults = 0 };
        Assert.Contains(config.Validate(), e => e.Contains("Max Browse Results", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsNegativeFuzzyMatchThreshold()
    {
        var config = new PluginConfiguration { FuzzyMatchThreshold = -1 };
        Assert.Contains(config.Validate(), e => e.Contains("Fuzzy Match Threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsOversizedFuzzyMatchThreshold()
    {
        var config = new PluginConfiguration { FuzzyMatchThreshold = 101 };
        Assert.Contains(config.Validate(), e => e.Contains("Fuzzy Match Threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AcceptsZeroFuzzyThresholds()
    {
        var config = new PluginConfiguration { FuzzyMatchThreshold = 0, FuzzySuggestionThreshold = 0 };
        Assert.DoesNotContain(config.Validate(), e => e.Contains("Threshold", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Tests for FuzzyMatcher config-backed methods.
/// </summary>
[Collection("Plugin")]
public class FuzzyMatcherConfigTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly PluginConfiguration _config;

    public FuzzyMatcherConfigTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
        _config = new PluginConfiguration();
        EnsurePluginInstance();
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    private void EnsurePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.Configuration.FuzzyMatchThreshold = _config.FuzzyMatchThreshold;
            Plugin.Instance.Configuration.FuzzySuggestionThreshold = _config.FuzzySuggestionThreshold;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-fuzzy-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<IApplicationPaths>();
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

        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(_config);

        var userManager = new Mock<IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            _loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }

    [Fact]
    public void GetDefaultThreshold_ReturnsDefault_WhenConfigNotCustomized()
    {
        Assert.Equal(60, FuzzyMatcher.GetDefaultThreshold());
    }

    [Fact]
    public void GetDefaultThreshold_ReturnsConfiguredValue()
    {
        _config.FuzzyMatchThreshold = 80;
        Plugin.Instance!.Configuration.FuzzyMatchThreshold = 80;

        Assert.Equal(80, FuzzyMatcher.GetDefaultThreshold());
    }

    [Fact]
    public void GetSuggestionThreshold_ReturnsDefault_WhenConfigNotCustomized()
    {
        Assert.Equal(40, FuzzyMatcher.GetSuggestionThreshold());
    }

    [Fact]
    public void GetSuggestionThreshold_ReturnsConfiguredValue()
    {
        _config.FuzzySuggestionThreshold = 20;
        Plugin.Instance!.Configuration.FuzzySuggestionThreshold = 20;

        Assert.Equal(20, FuzzyMatcher.GetSuggestionThreshold());
    }

    [Fact]
    public void GetDefaultThreshold_ReturnsZero_WhenConfiguredToZero()
    {
        _config.FuzzyMatchThreshold = 0;
        Plugin.Instance!.Configuration.FuzzyMatchThreshold = 0;

        Assert.Equal(0, FuzzyMatcher.GetDefaultThreshold());
    }
}
