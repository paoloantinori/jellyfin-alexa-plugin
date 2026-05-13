using System;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
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
        };

        Assert.Equal(50, config.MaxSearchResults);
        Assert.Equal(15, config.MaxBrowseResults);
        Assert.Equal(30, config.MaxRecentlyAddedResults);
        Assert.Equal(5, config.MaxRecommendationResults);
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
        };

        var errors = config.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("Search Results", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Browse Results", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Recently Added", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Recommendation Results", StringComparison.OrdinalIgnoreCase));
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

}

/// <summary>
/// Tests for FuzzyMatcher per-user threshold methods.
/// </summary>
public class FuzzyMatcherUserThresholdTests
{
    [Fact]
    public void GetDefaultThreshold_ReturnsConstant_WhenUserIsNull()
    {
        Assert.Equal(60, FuzzyMatcher.GetDefaultThreshold(null));
    }

    [Fact]
    public void GetDefaultThreshold_ReturnsUserValue_WhenSet()
    {
        var user = new Entities.User { FuzzyMatchThreshold = 80 };
        Assert.Equal(80, FuzzyMatcher.GetDefaultThreshold(user));
    }

    [Fact]
    public void GetDefaultThreshold_ReturnsUserValue_WhenZero()
    {
        var user = new Entities.User { FuzzyMatchThreshold = 0 };
        Assert.Equal(0, FuzzyMatcher.GetDefaultThreshold(user));
    }

    [Fact]
    public void GetSuggestionThreshold_ReturnsConstant_WhenUserIsNull()
    {
        Assert.Equal(40, FuzzyMatcher.GetSuggestionThreshold(null));
    }

    [Fact]
    public void GetSuggestionThreshold_ReturnsUserValue_WhenSet()
    {
        var user = new Entities.User { FuzzySuggestionThreshold = 20 };
        Assert.Equal(20, FuzzyMatcher.GetSuggestionThreshold(user));
    }
}
