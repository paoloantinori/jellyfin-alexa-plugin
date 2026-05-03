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

        // HttpClient for LWA and progressive responses
        serviceCollection.AddHttpClient("AlexaSkill");

        // Request pipeline interceptors
        serviceCollection.AddSingleton<IRequestInterceptor, LoggingRequestInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, SessionAttributesInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, LoggingResponseInterceptor>();

        // Request pipeline
        serviceCollection.AddSingleton<RequestPipeline>();
    }
}
