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
/// Tests for playback preference defaults in PluginConfiguration.
/// </summary>
public class PlaybackPreferenceDefaultsTests
{
    [Fact]
    public void PlaybackPreferences_HaveCorrectDefaults()
    {
        var config = new PluginConfiguration();

        Assert.Equal(5, config.InitialFetchSize);
        Assert.Equal(10, config.ContinuationBatchSize);
        Assert.Equal(2, config.PrefetchThreshold);
    }

    [Fact]
    public void PlaybackPreferences_CanBeCustomized()
    {
        var config = new PluginConfiguration
        {
            InitialFetchSize = 3,
            ContinuationBatchSize = 20,
            PrefetchThreshold = 5
        };

        Assert.Equal(3, config.InitialFetchSize);
        Assert.Equal(20, config.ContinuationBatchSize);
        Assert.Equal(5, config.PrefetchThreshold);
    }

    [Fact]
    public void Validate_AcceptsValidRanges()
    {
        var config = new PluginConfiguration
        {
            InitialFetchSize = 1,
            ContinuationBatchSize = 1,
            PrefetchThreshold = 0
        };

        var errors = config.Validate();
        Assert.DoesNotContain(errors, e => e.Contains("Fetch Size", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Batch Size", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, e => e.Contains("Threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsZeroInitialFetchSize()
    {
        var config = new PluginConfiguration { InitialFetchSize = 0 };
        Assert.Contains(config.Validate(), e => e.Contains("Initial Fetch Size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsOversizedInitialFetchSize()
    {
        var config = new PluginConfiguration { InitialFetchSize = 21 };
        Assert.Contains(config.Validate(), e => e.Contains("Initial Fetch Size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsZeroContinuationBatchSize()
    {
        var config = new PluginConfiguration { ContinuationBatchSize = 0 };
        Assert.Contains(config.Validate(), e => e.Contains("Continuation Batch Size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsOversizedContinuationBatchSize()
    {
        var config = new PluginConfiguration { ContinuationBatchSize = 51 };
        Assert.Contains(config.Validate(), e => e.Contains("Continuation Batch Size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsNegativePrefetchThreshold()
    {
        var config = new PluginConfiguration { PrefetchThreshold = -1 };
        Assert.Contains(config.Validate(), e => e.Contains("Pre-fetch Threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsOversizedPrefetchThreshold()
    {
        var config = new PluginConfiguration { PrefetchThreshold = 11 };
        Assert.Contains(config.Validate(), e => e.Contains("Pre-fetch Threshold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AcceptsZeroPrefetchThreshold()
    {
        var config = new PluginConfiguration { PrefetchThreshold = 0 };
        Assert.DoesNotContain(config.Validate(), e => e.Contains("Threshold", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Tests for ProgressiveQueueConstants config-backed methods.
/// </summary>
[Collection("Plugin")]
public class ProgressiveQueueConstantsTests : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly PluginConfiguration _config;

    public ProgressiveQueueConstantsTests()
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
            Plugin.Instance.Configuration.InitialFetchSize = _config.InitialFetchSize;
            Plugin.Instance.Configuration.ContinuationBatchSize = _config.ContinuationBatchSize;
            Plugin.Instance.Configuration.PrefetchThreshold = _config.PrefetchThreshold;
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-playback-test-" + Guid.NewGuid());
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
    public void GetInitialFetchSize_ReturnsDefault_WhenConfigNotCustomized()
    {
        Assert.Equal(5, ProgressiveQueueConstants.GetInitialFetchSize());
    }

    [Fact]
    public void GetInitialFetchSize_ReturnsConfiguredValue()
    {
        _config.InitialFetchSize = 3;
        Plugin.Instance!.Configuration.InitialFetchSize = 3;

        Assert.Equal(3, ProgressiveQueueConstants.GetInitialFetchSize());
    }

    [Fact]
    public void GetContinuationBatchSize_ReturnsDefault_WhenConfigNotCustomized()
    {
        Assert.Equal(10, ProgressiveQueueConstants.GetContinuationBatchSize());
    }

    [Fact]
    public void GetContinuationBatchSize_ReturnsConfiguredValue()
    {
        _config.ContinuationBatchSize = 25;
        Plugin.Instance!.Configuration.ContinuationBatchSize = 25;

        Assert.Equal(25, ProgressiveQueueConstants.GetContinuationBatchSize());
    }

    [Fact]
    public void GetPrefetchThreshold_ReturnsDefault_WhenConfigNotCustomized()
    {
        Assert.Equal(2, ProgressiveQueueConstants.GetPrefetchThreshold());
    }

    [Fact]
    public void GetPrefetchThreshold_ReturnsConfiguredValue()
    {
        _config.PrefetchThreshold = 5;
        Plugin.Instance!.Configuration.PrefetchThreshold = 5;

        Assert.Equal(5, ProgressiveQueueConstants.GetPrefetchThreshold());
    }

    [Fact]
    public void GetPrefetchThreshold_ReturnsZero_WhenConfiguredToZero()
    {
        _config.PrefetchThreshold = 0;
        Plugin.Instance!.Configuration.PrefetchThreshold = 0;

        Assert.Equal(0, ProgressiveQueueConstants.GetPrefetchThreshold());
    }

    [Fact]
    public void Constants_MatchDefaultMethods()
    {
        Assert.Equal(ProgressiveQueueConstants.DefaultInitialFetchSize, ProgressiveQueueConstants.GetInitialFetchSize());
        Assert.Equal(ProgressiveQueueConstants.DefaultContinuationBatchSize, ProgressiveQueueConstants.GetContinuationBatchSize());
        Assert.Equal(ProgressiveQueueConstants.DefaultPrefetchThreshold, ProgressiveQueueConstants.GetPrefetchThreshold());
    }
}
