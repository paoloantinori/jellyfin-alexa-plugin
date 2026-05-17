#nullable enable

using System;
using System.Net.Http;
using System.Text.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.ModelDeployment;

/// <summary>
/// Tests for <see cref="ModelDeploymentManager"/> validation and default model retrieval.
/// </summary>
public class ModelDeploymentManagerTests
{
    private readonly ModelDeploymentManager _sut;

    public ModelDeploymentManagerTests()
    {
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        var loggerMock = new Mock<ILogger<ModelDeploymentManager>>();
        _sut = new ModelDeploymentManager(httpClientFactoryMock.Object, loggerMock.Object);
    }

    // --- ValidateModelJson: valid inputs ---

    [Fact]
    public void ValidateModelJson_ValidWrappedFormat_ReturnsValid()
    {
        // Arrange
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new
            {
                languageModel = new
                {
                    invocationName = "test",
                    intents = new[]
                    {
                        new { name = "TestIntent", samples = new[] { "test" } },
                    },
                },
            },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(1, result.IntentCount);
        Assert.Equal("test", result.InvocationName);
        Assert.Empty(result.ErrorMessage);
    }

    [Fact]
    public void ValidateModelJson_ValidBareFormat_ReturnsValid()
    {
        // Arrange — no interactionModel wrapper, just languageModel at the root
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[]
                {
                    new { name = "TestIntent", samples = new[] { "test" } },
                },
            },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(1, result.IntentCount);
        Assert.Equal("test", result.InvocationName);
    }

    // --- ValidateModelJson: invalid inputs ---

    [Fact]
    public void ValidateModelJson_InvalidJson_ReturnsInvalid()
    {
        // Arrange
        string json = "this is not json";

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("Invalid JSON", result.ErrorMessage);
    }

    [Fact]
    public void ValidateModelJson_EmptyString_ReturnsInvalid()
    {
        // Act
        var result = _sut.ValidateModelJson(string.Empty);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateModelJson_WhitespaceOnly_ReturnsInvalid()
    {
        // Act
        var result = _sut.ValidateModelJson("   \t\n  ");

        // Assert
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateModelJson_MissingInvocationName_ReturnsInvalid()
    {
        // Arrange — no invocationName property
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new
            {
                languageModel = new
                {
                    intents = new[]
                    {
                        new { name = "TestIntent", samples = new[] { "test" } },
                    },
                },
            },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("invocationName", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateModelJson_EmptyInvocationName_ReturnsInvalid()
    {
        // Arrange — invocationName present but empty string
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new
            {
                languageModel = new
                {
                    invocationName = "",
                    intents = new[]
                    {
                        new { name = "TestIntent", samples = new[] { "test" } },
                    },
                },
            },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("invocationName", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateModelJson_MissingIntents_ReturnsInvalid()
    {
        // Arrange — no intents property at all
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new
            {
                languageModel = new
                {
                    invocationName = "test",
                },
            },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("intents", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("test", result.InvocationName);
    }

    [Fact]
    public void ValidateModelJson_EmptyIntentsArray_ReturnsInvalid()
    {
        // Arrange — intents array exists but is empty
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new
            {
                languageModel = new
                {
                    invocationName = "test",
                    intents = Array.Empty<object>(),
                },
            },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("intents", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateModelJson_MissingLanguageModel_ReturnsInvalid()
    {
        // Arrange — interactionModel present but no languageModel child
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new { },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("languageModel", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateModelJson_NoInteractionModelOrLanguageModel_ReturnsInvalid()
    {
        // Arrange — neither interactionModel nor languageModel at the root
        string json = JsonSerializer.Serialize(new { foo = "bar" });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("interactionModel", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("languageModel", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // --- ValidateModelJson: intent count ---

    [Fact]
    public void ValidateModelJson_MultipleIntents_ReturnsCorrectCount()
    {
        // Arrange — 5 intents
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new
            {
                languageModel = new
                {
                    invocationName = "test",
                    intents = new object[]
                    {
                        new { name = "Intent1", samples = new[] { "a" } },
                        new { name = "Intent2", samples = new[] { "b" } },
                        new { name = "Intent3", samples = new[] { "c" } },
                        new { name = "Intent4", samples = new[] { "d" } },
                        new { name = "Intent5", samples = new[] { "e" } },
                    },
                },
            },
        });

        // Act
        var result = _sut.ValidateModelJson(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(5, result.IntentCount);
    }

    // --- GetDefaultModelJson ---

    [Fact]
    public void GetDefaultModelJson_ExistingLocale_ReturnsNonNullJson()
    {
        // Act
        string? json = _sut.GetDefaultModelJson("en-US");

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);
        Assert.Contains("languageModel", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefaultModelJson_ExistingLocale_ContainsInvocationName()
    {
        // Act
        string? json = _sut.GetDefaultModelJson("en-US");

        // Assert
        Assert.NotNull(json);
        // The en-US model should have a valid invocationName
        Assert.Contains("invocationName", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefaultModelJson_NonexistentLocale_ReturnsNull()
    {
        // Act
        string? json = _sut.GetDefaultModelJson("xx-XX");

        // Assert
        Assert.Null(json);
    }
}
