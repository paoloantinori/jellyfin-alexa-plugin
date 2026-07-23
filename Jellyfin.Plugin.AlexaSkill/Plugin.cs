using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller.Handler;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private static readonly HttpClient _fallbackHttpClient = new();
    private string? _lastKnownServerAddress;
    private IHttpClientFactory? _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILoggerFactory loggerFactory,
        IUserManager userManager) : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        UserManager = userManager;
        LoggerFactory = loggerFactory;

        ConfigurationChanged += OnConfigurationChanged;
        _lastKnownServerAddress = Configuration.ServerAddress;

        ILogger<Plugin> logger = loggerFactory.CreateLogger<Plugin>();
        logger.LogInformation("AlexaSkill plugin loaded v{Version}", Util.GetVersion());

        ResponseStrings.SetLogger(loggerFactory.CreateLogger("Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings"));

        if (!RequestConverter.RequestConverters.Any(c => c is AplUserEventRequestConverter))
        {
            RequestConverter.RequestConverters.Add(new AplUserEventRequestConverter());
        }

        // JF-300: normalize legacy users whose stored InvocationName is the global
        // default so they get locale defaults (it-IT → "mia collezione") instead of
        // a "customized" name that would clobber the it-IT locale.
        MigrateDefaultInvocationNames(Configuration);
    }

    /// <inheritdoc />
    public override string Name => "AlexaSkill";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c5df7de0-8777-4b3c-a70d-5c3dae359c9e");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Reset the singleton instance. For test teardown only.
    /// </summary>
    internal static void ResetInstance() => Instance = null;

    /// <summary>
    /// Gets an HttpClient from the registered IHttpClientFactory.
    /// Falls back to a static HttpClient if DI is not available.
    /// </summary>
    public static HttpClient HttpClient
    {
        get
        {
            if (Instance?._httpClientFactory != null)
            {
                return Instance._httpClientFactory.CreateClient("AlexaSkill");
            }

            return _fallbackHttpClient;
        }
    }

    /// <summary>
    /// Gets an HttpClient for Alexa progressive responses: factory-backed (fresh per call,
    /// so ProgressiveResponse's BaseAddress assignment is safe) with a 2-second timeout.
    /// Falls back to a fresh per-call client with a 2s timeout when DI is unavailable — a
    /// per-call allocation is required here (not a static client) because ProgressiveResponse
    /// sets BaseAddress, which throws if the client has already been used. This fallback is a
    /// non-production escape hatch; the factory path is always used in a hosted Jellyfin instance.
    /// </summary>
    public static HttpClient HttpClientProgressive
    {
        get
        {
            if (Instance?._httpClientFactory != null)
            {
                return Instance._httpClientFactory.CreateClient("AlexaSkillProgressive");
            }

            return new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        }
    }

    /// <summary>
    /// Gets or sets the skill manifest.
    /// </summary>
    public ManifestSkill? ManifestSkill { get; set; }

    /// <summary>
    /// Gets the user manager for resolving Jellyfin users.
    /// </summary>
    public IUserManager UserManager { get; private set; }

    /// <summary>
    /// Gets the logger factory.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; private set; }

    /// <summary>
    /// Gets the LWA authorization request handler.
    /// </summary>
    public LwaAuthorizationRequestHandler LwaAuthorizationRequestHandler { get; } =
        new LwaAuthorizationRequestHandler();

    /// <summary>
    /// Gets the Interaction models for each supported locale.
    /// </summary>
    public Collection<Tuple<string, string>> InteractionModels { get; } = Util.GetLocalInteractionModels();

    /// <summary>
    /// Gets the CSRF token handler.
    /// </summary>
    public CsrfTokenHandler CsrfTokenHandler { get; } = new CsrfTokenHandler();

    /// <summary>
    /// Gets the search result cache for fallback on API failures.
    /// </summary>
    public SearchResultCache SearchCache { get; internal set; } = SearchResultCache.Noop;

    /// <summary>
    /// Gets the circuit breaker for tracking Jellyfin backend API health.
    /// </summary>
    public CircuitBreaker CircuitBreaker { get; internal set; } = new CircuitBreaker();

    /// <summary>
    /// Gets the connectivity checker for Jellyfin server health diagnostics.
    /// </summary>
    public JellyfinConnectivityChecker? ConnectivityChecker { get; internal set; }

    /// <summary>
    /// Gets the request counters for metrics tracking.
    /// </summary>
    public RequestCounters RequestCounters { get; internal set; } = new RequestCounters();

    /// <summary>
    /// Gets the per-device playback queue manager. Set from DI in SkillStartup.
    /// Accessed by BaseHandler to record last-played items at the play chokepoint.
    /// </summary>
    public Alexa.Playback.DeviceQueueManager? DeviceQueueManager { get; internal set; }

    /// <summary>
    /// Gets the global audiobook position tracker (segment-based). Set from DI in SkillStartup.
    /// Accessed by VideoAudioController to record segment requests and by the resume flow to
    /// read the tracked position.
    /// </summary>
    public Alexa.Playback.AudiobookPositionTracker? AudiobookPositionTracker { get; internal set; }

    /// <summary>
    /// Sets the HttpClientFactory from DI registration.
    /// </summary>
    internal IHttpClientFactory? HttpClientFactory
    {
        set => _httpClientFactory = value;
    }

    /// <summary>
    /// Builds the skill interaction model collection for a given invocation name.
    /// Each user gets their own skill with their own invocation name, which replaces
    /// the template's default in all locale models.
    ///
    /// Resolution (JF-300): an empty/whitespace <paramref name="invocationName"/>
    /// means "use locale defaults" — <see cref="Config.LocaleInvocationNames"/>
    /// (e.g. it-IT → "mia collezione"), falling back to <see cref="Config.InvocationName"/>
    /// ("jellyfin player") for all other locales. A non-empty custom name applies to
    /// <strong>all 17 locales</strong>, including it-IT.
    /// </summary>
    /// <param name="invocationName">The per-user invocation name, or empty/whitespace for locale defaults.</param>
    /// <returns>A collection of skill interaction models.</returns>
    public Collection<SkillInteractionModel> BuildSkillInteractionModels(string invocationName, string? localeFilter = null)
    {
        // Admin mood overrides (JF-355): inject custom mood words into the Mood slot
        // type of every locale model that HAS a Mood slot type so the NLU fills the
        // slot one-shot. Today only it-IT has the custom Mood type; the other 16 locales
        // gain it via JF-356 (InjectMoodSlotValues no-ops where no Mood type exists).
        // The handler resolves overrides regardless of locale via MoodGenreMap merge.
        // InjectMoodSlotValues trims/dedupes/filters, so pass the raw mood strings.
        IEnumerable<string> moodOverrideWords = (Configuration.MoodGenreOverrides ?? Enumerable.Empty<MoodGenreOverride>())
            .Where(o => o != null)
            .Select(o => o.Mood);

        Collection<SkillInteractionModel> models = new Collection<SkillInteractionModel>();
        foreach (Tuple<string, string> model in InteractionModels)
        {
            if (!string.IsNullOrWhiteSpace(localeFilter)
                && !string.Equals(model.Item1, localeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string localeInvocation = Config.EffectiveInvocationName(model.Item1, invocationName);
            var skillModel = new SkillInteractionModel(model.Item1, model.Item2, localeInvocation);
            skillModel.InjectMoodSlotValues(moodOverrideWords);
            models.Add(skillModel);
        }

        return models;
    }

    /// <summary>
    /// One-time migration (JF-300): clears each user's stored
    /// <see cref="Entities.UserSkill.InvocationName"/> when it equals the global
    /// default (<see cref="Config.InvocationName"/>). Pre-JF-300 every user had
    /// "jellyfin player" persisted, which — under the new empty=default rule —
    /// would be treated as a custom name and overwrite it-IT's "mia collezione"
    /// (a regression). Clearing it makes those users fall back to locale defaults.
    ///
    /// The string comparison against the literal default lives ONLY here.
    /// </summary>
    internal static void MigrateDefaultInvocationNames(Configuration.PluginConfiguration configuration)
    {
        bool changed = false;
        foreach (var user in configuration.Users)
        {
            if (user.UserSkill != null && Config.IsStoredGlobalDefault(user.UserSkill.InvocationName))
            {
                user.UserSkill.InvocationName = string.Empty;
                changed = true;
            }
        }

        if (changed && Instance != null)
        {
            // Persist the normalized config so the migration does not repeat every load.
            try
            {
                Instance.SaveConfiguration();
            }
            catch (Exception)
            {
                // Persistence is best-effort during load; the in-memory fix is still effective.
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
            }
        };
    }

    /// <summary>
    /// Handle configuration changes by propagating them to active services.
    /// Only resets caches when ServerAddress actually changes.
    /// </summary>
    private async void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        var config = (PluginConfiguration)e;
        var logger = LoggerFactory.CreateLogger<Plugin>();

        if (string.Equals(_lastKnownServerAddress, config.ServerAddress, StringComparison.Ordinal))
        {
            return;
        }

        _lastKnownServerAddress = config.ServerAddress;
        logger.LogInformation("ServerAddress changed — propagating to active services");

        if (ConnectivityChecker != null)
        {
            await ConnectivityChecker.InvalidateCacheAsync().ConfigureAwait(false);
        }

        CircuitBreaker.Reset();
        SearchCache.Clear();
    }
}
