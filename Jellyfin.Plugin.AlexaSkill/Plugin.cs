using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
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
    }

    /// <inheritdoc />
    public override string Name => "AlexaSkill";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c5df7de0-8777-4b3c-a70d-5c3dae359c9e");

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

    private static readonly HttpClient _fallbackHttpClient = new();

    /// <summary>
    /// Sets the HttpClientFactory from DI registration.
    /// </summary>
    internal IHttpClientFactory? _httpClientFactory;

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
    /// Gets or sets the skill manifest.
    /// </summary>
    public ManifestSkill? ManifestSkill { get; set; }

    /// <summary>
    /// Gets the dictionary of device ids to session tokens.
    /// </summary>
    public IUserManager UserManager { get; private set; }

    /// <summary>
    /// Gets the logger factory.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; private set; }

    /// <summary>
    /// Gets the dictionary of device ids to session tokens.
    /// </summary>
    public Dictionary<string, string> SessionTokens { get; } = new Dictionary<string, string>();

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
    /// Builds the skill interaction model collection for a given invocation name.
    /// Each user gets their own skill with their own invocation name, which replaces
    /// the template's default in all locale models.
    /// </summary>
    /// <param name="invocationName">The invocation name for the skill.</param>
    /// <returns>A collection of skill interaction models.</returns>
    public Collection<SkillInteractionModel> BuildSkillInteractionModels(string invocationName)
    {
        Collection<SkillInteractionModel> models = new Collection<SkillInteractionModel>();
        foreach (Tuple<string, string> model in InteractionModels)
        {
            models.Add(new SkillInteractionModel(model.Item1, model.Item2, invocationName));
        }

        return models;
    }

    /// <summary>
    /// Gets the CSRF token handler.
    /// </summary>
    public CsrfTokenHandler CsrfTokenHandler { get; } = new CsrfTokenHandler();

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    private string? _lastKnownServerAddress;

    /// <summary>
    /// Handle configuration changes by propagating them to active services.
    /// Only resets caches when ServerAddress actually changes.
    /// </summary>
    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        var config = (PluginConfiguration)e;
        var logger = LoggerFactory.CreateLogger<Plugin>();

        if (string.Equals(_lastKnownServerAddress, config.ServerAddress, StringComparison.Ordinal))
        {
            return;
        }

        _lastKnownServerAddress = config.ServerAddress;
        logger.LogInformation("ServerAddress changed — propagating to active services");

        ConnectivityChecker?.InvalidateCache();
        CircuitBreaker.Reset();
        SearchCache.Clear();
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
}
