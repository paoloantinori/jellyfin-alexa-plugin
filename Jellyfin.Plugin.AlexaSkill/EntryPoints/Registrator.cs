using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Register plugin services with the Jellyfin DI container.
/// </summary>
public class Registrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Background services
        serviceCollection.AddHostedService<SkillStartup>();

        // Singletons
        serviceCollection.AddSingleton<RequestCounters>();
        serviceCollection.AddSingleton<SearchResultCache>();
        serviceCollection.AddSingleton<CircuitBreaker>();

        // HttpClient for LWA and progressive responses
        serviceCollection.AddHttpClient("AlexaSkill");

        // Request pipeline interceptors (order matters: circuit breaker first for fail-fast)
        serviceCollection.AddSingleton<IRequestInterceptor, CircuitBreakerInterceptor>();
        serviceCollection.AddSingleton<IRequestInterceptor, LoggingRequestInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, SessionAttributesInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, LoggingResponseInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, MetricsResponseInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, ResponseBodyLoggingInterceptor>();

        // Request pipeline
        serviceCollection.AddSingleton<RequestPipeline>();
    }
}
