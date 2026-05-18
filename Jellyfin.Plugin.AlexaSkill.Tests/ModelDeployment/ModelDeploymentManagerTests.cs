#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

    // --- ValidateSMAPIRestrictions ---

    [Fact]
    public void ValidateSMAPIRestrictions_EmptySlotType_ReturnsError()
    {
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                types = new object[]
                {
                    new { name = "EmptyType", values = Array.Empty<object>() },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, "it-IT");

        Assert.Single(errors);
        Assert.Contains("EmptyType", errors[0]);
        Assert.Contains("no values", errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSMAPIRestrictions_SlotTypeWithValues_NoError()
    {
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                types = new object[]
                {
                    new
                    {
                        name = "MediaType",
                        values = new[] { new { name = new { value = "song" } } },
                    },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, "it-IT");

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateSMAPIRestrictions_SensitivityInNonEnglishLocale_ReturnsError()
    {
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                modelConfiguration = new
                {
                    fallbackIntentSensitivity = new { level = "LOW" },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, "it-IT");

        Assert.Single(errors);
        Assert.Contains("fallbackIntentSensitivity", errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("it-IT", errors[0]);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("en-AU")]
    [InlineData("de-DE")]
    public void ValidateSMAPIRestrictions_SensitivityInSupportedLocale_NoError(string locale)
    {
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                modelConfiguration = new
                {
                    fallbackIntentSensitivity = new { level = "LOW" },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, locale);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [InlineData("pt-BR")]
    [InlineData("ar-SA")]
    public void ValidateSMAPIRestrictions_SensitivityInUnsupportedLocale_ReturnsError(string locale)
    {
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                modelConfiguration = new
                {
                    fallbackIntentSensitivity = new { level = "LOW" },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, locale);

        Assert.Single(errors);
    }

    [Fact]
    public void ValidateSMAPIRestrictions_MultipleViolations_ReturnsAllErrors()
    {
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                types = new object[]
                {
                    new { name = "EmptyType", values = Array.Empty<object>() },
                },
                modelConfiguration = new
                {
                    fallbackIntentSensitivity = new { level = "LOW" },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, "fr-FR");

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void ValidateSMAPIRestrictions_ValidModel_NoErrors()
    {
        string json = JsonSerializer.Serialize(new
        {
            languageModel = new
            {
                invocationName = "test",
                intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                types = new object[]
                {
                    new
                    {
                        name = "MediaType",
                        values = new[] { new { name = new { value = "song" } } },
                    },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, "en-US");

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateSMAPIRestrictions_WrappedModel_Works()
    {
        string json = JsonSerializer.Serialize(new
        {
            interactionModel = new
            {
                languageModel = new
                {
                    invocationName = "test",
                    intents = new[] { new { name = "TestIntent", samples = new[] { "test" } } },
                    types = new object[]
                    {
                        new { name = "EmptyType", values = Array.Empty<object>() },
                    },
                },
            },
        });

        var errors = _sut.ValidateSMAPIRestrictions(json, "it-IT");

        Assert.Single(errors);
        Assert.Contains("EmptyType", errors[0]);
    }

    // --- FetchModelJsonAsync ---

    [Fact]
    public async Task FetchModelJsonAsync_NullUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.FetchModelJsonAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task FetchModelJsonAsync_EmptyUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.FetchModelJsonAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task FetchModelJsonAsync_WhitespaceUrl_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.FetchModelJsonAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task FetchModelJsonAsync_ValidUrl_ReturnsJson()
    {
        // Arrange
        var expectedJson = "{\"interactionModel\":{\"languageModel\":{\"invocationName\":\"test\"}}}";
        var handler = new MockHttpMessageHandler(expectedJson);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("AlexaSkill")).Returns(client);
        var sut = new ModelDeploymentManager(factory.Object, Mock.Of<ILogger<ModelDeploymentManager>>());

        // Act
        string result = await sut.FetchModelJsonAsync("http://localhost/model.json", CancellationToken.None);

        // Assert
        Assert.Equal(expectedJson, result);
    }

    [Fact]
    public async Task FetchModelJsonAsync_HttpError_ThrowsHttpRequestException()
    {
        // Arrange
        var handler = new MockHttpMessageHandler("", System.Net.HttpStatusCode.InternalServerError);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("AlexaSkill")).Returns(client);
        var sut = new ModelDeploymentManager(factory.Object, Mock.Of<ILogger<ModelDeploymentManager>>());

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.FetchModelJsonAsync("http://localhost/model.json", CancellationToken.None));
    }

    [Fact]
    public async Task FetchModelJsonAsync_EmptyBody_ThrowsInvalidOperationException()
    {
        // Arrange — server returns 200 but empty body
        var handler = new MockHttpMessageHandler("", System.Net.HttpStatusCode.OK);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("AlexaSkill")).Returns(client);
        var sut = new ModelDeploymentManager(factory.Object, Mock.Of<ILogger<ModelDeploymentManager>>());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.FetchModelJsonAsync("http://localhost/model.json", CancellationToken.None));
    }

    // --- GetDefaultModelJson: all supported locales ---

    public static IEnumerable<object[]> AllModelLocales()
    {
        foreach (var model in Util.GetLocalInteractionModels())
        {
            yield return new object[] { model.Item1 };
        }
    }

    public static IEnumerable<object[]> NonEnglishGermanLocales()
    {
        foreach (var model in Util.GetLocalInteractionModels())
        {
            if (!model.Item1.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
                && !model.Item1.Equals("de-DE", StringComparison.OrdinalIgnoreCase))
            {
                yield return new object[] { model.Item1 };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllModelLocales))]
    public void GetDefaultModelJson_AllSupportedLocales_ReturnsValidModel(string locale)
    {
        string? json = _sut.GetDefaultModelJson(locale);

        Assert.NotNull(json);
        var validationResult = _sut.ValidateModelJson(json!);
        Assert.True(validationResult.IsValid, $"Model for {locale} failed validation: {validationResult.ErrorMessage}");
        Assert.True(validationResult.IntentCount > 0, $"Model for {locale} has 0 intents");
    }

    [Theory]
    [MemberData(nameof(NonEnglishGermanLocales))]
    public void ValidateSMAPIRestrictions_EmbeddedModel_NoErrors(string locale)
    {
        string? json = _sut.GetDefaultModelJson(locale);
        Assert.NotNull(json);

        var errors = _sut.ValidateSMAPIRestrictions(json!, locale);
        Assert.Empty(errors);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly System.Net.HttpStatusCode _statusCode;

        public MockHttpMessageHandler(string content, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _content = content;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
