using System;
using System.Net.Http;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-314 unit tests verifying the dedicated AlexaSkillProgressive HttpClient
/// is factory-backed with a 2-second timeout and returns fresh instances per call.
/// <para>
/// These tests provision their OWN <c>IHttpClientFactory</c> with the
/// <c>"AlexaSkillProgressive"</c> named client, so they verify the
/// <c>Plugin.HttpClientProgressive</c> accessor contract (2s timeout, distinct
/// instance per call) but do NOT verify that
/// <c>Registrator.RegisterServices</c> registers the named client in production
/// — that DI wiring lives in <c>EntryPoints/Registrator.cs</c>.
/// </para>
/// </summary>
[Collection("Plugin")]
public class HttpClientProgressiveTests : PluginTestBase, IDisposable
{
    private readonly IHttpClientFactory _factory;
    private readonly ILoggerFactory _loggerFactory;

    public HttpClientProgressiveTests()
    {
        _loggerFactory = LoggerFactory.Create(_ => { });

        var services = new ServiceCollection();
        services.AddHttpClient("AlexaSkillProgressive")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(2));

        var provider = services.BuildServiceProvider();
        _factory = provider.GetRequiredService<IHttpClientFactory>();

        // Seed Plugin.Instance with a minimal plugin and wire the factory
        SeedPluginInstance();
    }

    public void Dispose()
    {
        Plugin.ResetInstance();
        _loggerFactory.Dispose();
    }

    private void SeedPluginInstance()
    {
        TestHelpers.EnsurePluginInstance(new PluginConfiguration(), _loggerFactory, _ => { }, "alexa-http-prog-test");
        Plugin.Instance!.HttpClientFactory = _factory;
    }

    [Fact]
    public void HttpClientProgressive_HasTwoSecondTimeout()
    {
        var client = Plugin.HttpClientProgressive;
        Assert.Equal(TimeSpan.FromSeconds(2), client.Timeout);
    }

    [Fact]
    public void HttpClientProgressive_ReturnsDistinctInstancesPerCall()
    {
        var client1 = Plugin.HttpClientProgressive;
        var client2 = Plugin.HttpClientProgressive;
        Assert.NotSame(client1, client2);
    }
}
