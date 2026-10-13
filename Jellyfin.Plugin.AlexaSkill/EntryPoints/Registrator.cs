using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using MediaBrowser.Common.Configuration;
using Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.ProactiveEvents;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        serviceCollection.AddSingleton<DeviceQueueManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<DeviceQueueManager>();
            string dataDir = Path.Combine(Plugin.Instance!.DataFolderPath, "queues");
            return new DeviceQueueManager(dataDir, logger);
        });
        serviceCollection.AddSingleton<Alexa.Playback.AudiobookPositionTracker>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<Alexa.Playback.AudiobookPositionTracker>();
            string dataDir = Path.Combine(Plugin.Instance!.DataFolderPath, "queues");
            return new Alexa.Playback.AudiobookPositionTracker(dataDir, logger);
        });
        serviceCollection.AddSingleton<RequestCounters>();
        serviceCollection.AddSingleton<SearchResultCache>();
        // JF-645 item 3: TTL cache for the JF-643 kana genre-resolution tier's
        // vocabulary (optional ctor param on the genre handlers; null disables caching).
        serviceCollection.AddSingleton<Alexa.Cache.GenreVocabularyCache>();
        serviceCollection.AddSingleton<CircuitBreaker>();
        serviceCollection.AddSingleton<JellyfinConnectivityChecker>();

        // Video-audio MP4 cache
        serviceCollection.AddSingleton<VideoAudioCache>(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<VideoAudioCache>();
            return new VideoAudioCache(appPaths, logger);
        });

        // In-memory artist index (also registered as hosted service for startup loading)
        serviceCollection.AddSingleton<ArtistIndexService>();
        serviceCollection.AddSingleton<IArtistIndex>(sp => sp.GetRequiredService<ArtistIndexService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<ArtistIndexService>());

        // In-memory song n-gram index (also registered as hosted service for startup loading)
        serviceCollection.AddSingleton<SongNgramIndexService>();
        serviceCollection.AddSingleton<ISongNgramIndex>(sp => sp.GetRequiredService<SongNgramIndexService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SongNgramIndexService>());

        // Scheduled tasks (visible in Jellyfin dashboard)
        serviceCollection.AddSingleton<TokenRefreshTask>();
        serviceCollection.AddSingleton<CacheCleanupTask>();
        serviceCollection.AddSingleton<CatalogSyncTask>();

        // Catalog sync services
        serviceCollection.AddSingleton<CatalogManager>();
        serviceCollection.AddSingleton<LibrarySyncService>();

        // Dynamic entity resolution
        serviceCollection.AddSingleton<DynamicEntityBuilder>();

        // Custom model deployment
        serviceCollection.AddSingleton<ModelDeploymentManager>();

        // Interaction-model redeploy (invocation-name changes via settings, manual rebuild)
        serviceCollection.AddSingleton<IInteractionModelRedeployer, InteractionModelRedeployer>();

        // Proactive events
        serviceCollection.AddSingleton<ProactiveEventClient>();
        serviceCollection.AddSingleton<ProactiveEventRateLimiter>();
        serviceCollection.AddHostedService<ProactiveEventService>();

        // PluginConfiguration — resolves live from Plugin.Instance
        serviceCollection.AddTransient(_ => Plugin.Instance!.Configuration);

        // HttpClient for LWA and progressive responses
        serviceCollection.AddHttpClient("AlexaSkill");

        // Dedicated client for Alexa progressive responses: fresh per call (ProgressiveResponse
        // sets BaseAddress internally, which is only safe on a never-used client) and capped at 2s
        // so a stalled Alexa API call cannot linger. (JF-314)
        serviceCollection.AddHttpClient("AlexaSkillProgressive")
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(2));

        // Live TV stream resolver (PlaybackInfo -> direct-remote vs dynamic HLS master)
        serviceCollection.AddSingleton<ILiveTvStreamResolver, LiveTvStreamResolver>();

        // Intent / event / error handlers — auto-discovered from this assembly
        foreach (var handlerType in RegisteredHandlerTypes())
        {
            serviceCollection.AddSingleton(typeof(BaseHandler), handlerType);
        }

        // JF-624 item 3: typed singletons for the two handlers the APL
        // disambiguation taps delegate to. These are SEPARATE instances from the
        // BaseHandler-keyed ones above: resolving a sibling through the
        // BaseHandler key from inside AplUserEventHandler's constructor is a DI
        // cycle (enumerating all BaseHandler services mid-construction), and
        // forwarding factories would strip the ImplementationType the
        // RealRegistrator dispatch-mirror pin reads. The dual instances are safe
        // under the BaseHandler singleton-statelessness rule (its class remark):
        // a handler with mutable per-request state is a bug for the voice
        // dispatch path too, not just the tap path. Both types sit in the
        // Handler namespace despite living in the Intent folder.
        serviceCollection.AddSingleton<Alexa.Handler.YesIntentHandler>();
        serviceCollection.AddSingleton<Alexa.Handler.FindSongIntentHandler>();
        // JF-624 item 4: the two paginated-picker tap targets (chapter and episode
        // picking), same typed-singleton rule as above.
        serviceCollection.AddSingleton<Alexa.Handler.GoToChapterIntentHandler>();
        serviceCollection.AddSingleton<Alexa.Handler.PlayEpisodeIntentHandler>();
        // JF-876: the song-carrier misroute delegation target (PlayVideoIntentHandler
        // hands a misrouted song ask to it; JF-882 extended the guard family to
        // PlayBook, PlayEpisode, and PlayAlbum, all REQUIRED ctor dependents),
        // same typed-singleton rule as above.
        serviceCollection.AddSingleton<Alexa.Handler.PlaySongIntentHandler>();

        // Request pipeline interceptors (order matters: circuit breaker first for fail-fast,
        // then audio device capability check before any Jellyfin API calls)
        serviceCollection.AddSingleton<IRequestInterceptor, CircuitBreakerInterceptor>();
        serviceCollection.AddSingleton<IRequestInterceptor, AudioDeviceCapabilityInterceptor>();
        serviceCollection.AddSingleton<IRequestInterceptor, LoggingRequestInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, SessionAttributesInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, DynamicEntitiesInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, LastPlayedResponseInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, LoggingResponseInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, MetricsResponseInterceptor>();
        serviceCollection.AddSingleton<IResponseInterceptor, ResponseBodyLoggingInterceptor>();
        // JF-846: registered LAST so it executes FIRST in the reverse-order
        // response chain (see the interceptor's doc comment): the prefix must
        // land before ResponseBodyLoggingInterceptor's snapshot so the logged
        // body shows what the user heard.
        serviceCollection.AddSingleton<IResponseInterceptor, SkillMaintenanceInterceptor>();

        // Request pipeline
        serviceCollection.AddSingleton<RequestPipeline>();
    }

    /// <summary>
    /// Enumerates the intent/event/error handler types in dispatch registration order:
    /// FallbackIntentHandler last (so it never preempts a specific handler), everything
    /// else alphabetical by type name. The controller's first-CanHandle-wins dispatch
    /// iterates this order; the routing test harness reuses the same enumeration
    /// instead of mirroring it (JF-452).
    /// </summary>
    /// <returns>The handler types in registration order.</returns>
    internal static IReadOnlyList<Type> RegisteredHandlerTypes()
    {
        Type[] allTypes;
        try
        {
            allTypes = typeof(BaseHandler).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            allTypes = ex.Types.Where(t => t is not null).ToArray()!;
        }

        return allTypes
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(BaseHandler)))
            .OrderBy(t => t.Name == nameof(FallbackIntentHandler) ? 1 : 0)
            .ThenBy(t => t.Name)
            .ToList();
    }
}
