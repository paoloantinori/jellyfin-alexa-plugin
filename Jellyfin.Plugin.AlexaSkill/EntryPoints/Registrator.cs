using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
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

        // PluginConfiguration — resolves live from Plugin.Instance
        serviceCollection.AddTransient(_ => Plugin.Instance!.Configuration);

        // HttpClient for LWA and progressive responses
        serviceCollection.AddHttpClient("AlexaSkill");

        // Intent / event / error handlers — auto-discovered from this assembly
        Type[] allTypes;
        try
        {
            allTypes = typeof(BaseHandler).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t is not null).ToArray()!;
        }

        var handlerTypes = allTypes
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(BaseHandler)))
            .OrderBy(t => t.Name);

        foreach (var handlerType in handlerTypes)
        {
            serviceCollection.AddSingleton(typeof(BaseHandler), handlerType);
        }

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
