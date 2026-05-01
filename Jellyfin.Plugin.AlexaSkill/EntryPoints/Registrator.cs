using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Register background tasks
/// </summary>
public class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<SkillStartup>();
        serviceCollection.AddSingleton<RequestCounters>();
    }
}
