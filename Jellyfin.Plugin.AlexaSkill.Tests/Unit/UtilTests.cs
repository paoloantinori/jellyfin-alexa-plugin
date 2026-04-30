using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class UtilTests
{
    [Fact]
    public void GetLocalInteractionModels_ReturnsCollection_WithEnUs()
    {
        var models = Util.GetLocalInteractionModels();

        Assert.NotNull(models);
        Assert.NotEmpty(models);
        Assert.Contains(models, m => m.Item1 == "en-US");
    }

    [Fact]
    public void GetLocalInteractionModels_LocaleMatchesResourcePath()
    {
        var models = Util.GetLocalInteractionModels();

        foreach (var model in models)
        {
            Assert.False(string.IsNullOrEmpty(model.Item1), "Locale should not be empty");
            Assert.True(model.Item2.Contains("model_", StringComparison.Ordinal),
                $"Resource path '{model.Item2}' should contain 'model_'");
            Assert.True(model.Item2.EndsWith(".json", StringComparison.Ordinal),
                $"Resource path '{model.Item2}' should end with '.json'");
        }
    }

    [Theory]
    [InlineData(null!)]
    [InlineData("")]
    public void DeserializeFromFile_InvalidPath_ThrowsArgumentException(string path)
    {
        Assert.Throws<ArgumentException>(() => Util.DeserializeFromFile<string>(path));
    }

    [Fact]
    public void DeserializeFromFile_NonexistentPath_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            Util.DeserializeFromFile<string>("nonexistent.resource.json"));
    }
}
