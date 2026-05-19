using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class BaseHandlerAsrFallbackTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public BaseHandlerAsrFallbackTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private TestAsrFallbackHandler CreateHandler() =>
        new(_sessionManagerMock.Object, _config, _loggerFactory);

    [Fact]
    public async Task OriginalQueryReturnsResults_ReturnsImmediately_NoVariantsTried()
    {
        // Arrange
        var handler = CreateHandler();
        var originalResults = new List<string> { "result1", "result2" };
        var calls = new List<string>();

        Task<IReadOnlyList<string>> SearchFunc(string q)
        {
            calls.Add(q);
            return Task.FromResult<IReadOnlyList<string>>(originalResults);
        }

        // Act
        var result = await handler.TestSearchWithAsrFallbackAsync("pink floyd", SearchFunc);

        // Assert
        Assert.Equal(originalResults, result);
        Assert.Single(calls);
        Assert.Equal("pink floyd", calls[0]);
    }

    [Fact]
    public async Task OriginalEmpty_FeatureEnabled_VariantFindsResults_ReturnsVariantResults()
    {
        // Arrange
        _config.AsrCompoundWordFixEnabled = true;
        var handler = CreateHandler();
        var variantResults = new List<string> { "pinkfloyd match" };
        var calls = new List<string>();

        Task<IReadOnlyList<string>> SearchFunc(string q)
        {
            calls.Add(q);
            // "pinkfloyd" (first pairwise join variant) returns results
            if (q == "pinkfloyd")
            {
                return Task.FromResult<IReadOnlyList<string>>(variantResults);
            }

            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // Act
        var result = await handler.TestSearchWithAsrFallbackAsync("pink floyd", SearchFunc);

        // Assert
        Assert.Equal(variantResults, result);
        // Should have called: "pink floyd" (original), then "pinkfloyd" (first variant)
        Assert.True(calls.Count >= 2, $"Expected at least 2 calls, got {calls.Count}: {string.Join(", ", calls)}");
        Assert.Equal("pink floyd", calls[0]);
        Assert.Equal("pinkfloyd", calls[1]);
    }

    [Fact]
    public async Task OriginalEmpty_FeatureDisabled_ReturnsEmpty_NoVariantsTried()
    {
        // Arrange
        _config.AsrCompoundWordFixEnabled = false;
        var handler = CreateHandler();
        var calls = new List<string>();

        Task<IReadOnlyList<string>> SearchFunc(string q)
        {
            calls.Add(q);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // Act
        var result = await handler.TestSearchWithAsrFallbackAsync("pink floyd", SearchFunc);

        // Assert
        Assert.Empty(result);
        Assert.Single(calls);
        Assert.Equal("pink floyd", calls[0]);
    }

    [Fact]
    public async Task StopsOnFirstVariantWithResults_DoesNotCallSubsequentVariants()
    {
        // Arrange - "soul coughing" generates variants: "soulcoughing", "soulcoughing" (collapsed)
        // AsrVariantGenerator for "soul coughing":
        //   pairwise: "soulcoughing" (join words 0+1)
        //   collapsed: "soulcoughing" (deduplicated with pairwise)
        // So only 1 unique variant. Use 3 words for a better test.
        // "pink floyd band" generates:
        //   pairwise[0]: "pinkfloyd band"
        //   pairwise[1]: "pink pinkfloyd" -> no, "pink floydband"
        //   collapsed: "pinkfloydband"
        _config.AsrCompoundWordFixEnabled = true;
        var handler = CreateHandler();
        var firstVariantResults = new List<string> { "pinkfloyd band match" };
        var calls = new List<string>();

        Task<IReadOnlyList<string>> SearchFunc(string q)
        {
            calls.Add(q);
            if (q == "pinkfloyd band")
            {
                return Task.FromResult<IReadOnlyList<string>>(firstVariantResults);
            }

            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // Act
        var result = await handler.TestSearchWithAsrFallbackAsync("pink floyd band", SearchFunc);

        // Assert
        Assert.Equal(firstVariantResults, result);
        Assert.Equal("pink floyd band", calls[0]); // original
        Assert.Equal("pinkfloyd band", calls[1]);   // first variant (found)
        // Should NOT call "pink floydband" or "pinkfloydband"
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task AllVariantsEmpty_ReturnsOriginalEmptyResults()
    {
        // Arrange
        _config.AsrCompoundWordFixEnabled = true;
        var handler = CreateHandler();
        var originalEmpty = new List<string>();
        var calls = new List<string>();

        Task<IReadOnlyList<string>> SearchFunc(string q)
        {
            calls.Add(q);
            return Task.FromResult<IReadOnlyList<string>>(originalEmpty);
        }

        // Act
        var result = await handler.TestSearchWithAsrFallbackAsync("pink floyd", SearchFunc);

        // Assert
        Assert.Empty(result);
        // "pink floyd" (original) + "pinkfloyd" (only unique variant)
        Assert.Equal(2, calls.Count);
        Assert.Equal("pink floyd", calls[0]);
    }

    [Fact]
    public async Task SingleWordQuery_FeatureEnabled_NoVariants_ReturnsEmpty()
    {
        // Arrange
        _config.AsrCompoundWordFixEnabled = true;
        var handler = CreateHandler();
        var calls = new List<string>();

        Task<IReadOnlyList<string>> SearchFunc(string q)
        {
            calls.Add(q);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        // Act
        var result = await handler.TestSearchWithAsrFallbackAsync("radiohead", SearchFunc);

        // Assert
        Assert.Empty(result);
        Assert.Single(calls);
        Assert.Equal("radiohead", calls[0]);
    }

    /// <summary>
    /// Test handler that exposes the protected SearchWithAsrFallbackAsync method.
    /// </summary>
    private class TestAsrFallbackHandler : BaseHandler
    {
        public TestAsrFallbackHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory) { }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public Task<IReadOnlyList<T>> TestSearchWithAsrFallbackAsync<T>(
            string query, Func<string, Task<IReadOnlyList<T>>> searchFunc)
            => SearchWithAsrFallbackAsync(query, searchFunc);
    }
}
