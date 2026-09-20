#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request.Type;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;

/// <summary>
/// Response interceptor that injects dynamic entity values into the Alexa NLU.
/// On new sessions, injects artists, albums, and last-played items.
/// Mid-session, conditionally injects series or audiobook entities when the
/// conversation context suggests TV/book usage.
/// </summary>
public class DynamicEntitiesInterceptor : IResponseInterceptor
{
    private readonly DynamicEntityBuilder _builder;
    private readonly PluginConfiguration _config;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DynamicEntitiesInterceptor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicEntitiesInterceptor"/> class.
    /// </summary>
    /// <param name="builder">The dynamic entity builder.</param>
    /// <param name="config">The plugin configuration for user resolution.</param>
    /// <param name="libraryManager">Library manager for resolving the user's library scope.</param>
    /// <param name="logger">Logger instance.</param>
    public DynamicEntitiesInterceptor(
        DynamicEntityBuilder builder,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILogger<DynamicEntitiesInterceptor> logger)
    {
        _builder = builder;
        _config = config;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (context.Response?.Response == null)
        {
            return Task.CompletedTask;
        }

        // JF-419.2: warming refusals must not pay cold library queries on the way out
        if (context.SkipColdLibraryWork)
        {
            return Task.CompletedTask;
        }

        // AudioPlayer directives carry their own dialog state. Skip DynamicEntities for those.
        if (context.Response.Response.Directives?.Any(d =>
            d is AudioPlayerPlayDirective or StopDirective or ClearQueueDirective) == true)
        {
            return Task.CompletedTask;
        }

        // Amazon rejects a Dialog.UpdateDynamicEntities ride-along on a response that
        // already carries another Dialog.* directive (ElicitSlot/ConfirmSlot/Delegate):
        // INVALID_RESPONSE "No other directives are allowed to be specified with a Dialog
        // directive" (live 2026-08-21, new-session FindSong elicitation). Skip instead;
        // the entities refresh simply lands on a later turn. Matched by Type prefix
        // because the Dialog directive classes share no common base type.
        if (context.Response.Response.Directives?.Any(d =>
                d.Type?.StartsWith("Dialog.", StringComparison.Ordinal) == true
                && d is not DynamicEntitiesDirective) == true)
        {
            return Task.CompletedTask;
        }

        bool isNewSession = context.SkillRequest is LaunchRequest
            || (context.AlexaSession?.New ?? false);

        string intentName = context.IntentName;

        // Built-in playback-control intents carry no slot to resolve and must never
        // trigger a whole-library dynamic-entity refresh (issue #10 follow-up:
        // ShuffleOn arrived on a fresh session and the new-session path leaked the
        // entire catalog — artists/songs not in the playing playlist).
        if (IsPlaybackControlIntent(intentName))
        {
            return Task.CompletedTask;
        }

        // Determine if we should inject conditional entities
        bool includeSeries = false;
        bool includeAudiobooks = false;

        if (!isNewSession)
        {
            // Only inject mid-session if the intent suggests TV or book context
            includeSeries = DynamicEntityBuilder.IsTvContext(intentName);
            includeAudiobooks = DynamicEntityBuilder.IsBookContext(intentName);

            if (!includeSeries && !includeAudiobooks)
            {
                return Task.CompletedTask;
            }
        }

        // Inside the try (review round 2): user resolution failing must degrade via
        // the interceptor's own catch (which names the purpose), not the pipeline's
        // generic per-interceptor swallow that hid the JF-588 NRE.
        Guid? scopedUserId = null;
        try
        {
            var (jellyfinUserId, resolveScope, boundLibraryId) = ResolveUserWithLibraryScope(context);
            if (jellyfinUserId == Guid.Empty)
            {
                return Task.CompletedTask;
            }

            scopedUserId = jellyfinUserId;
            DynamicEntitiesDirective? directive = _builder.Build(jellyfinUserId, context.Locale, resolveScope, includeSeries, includeAudiobooks, cancellationToken, boundLibraryId);

            if (directive == null)
            {
                return Task.CompletedTask;
            }

            context.Response.Response.Directives ??= new List<global::Alexa.NET.Response.IDirective>();
            context.Response.Response.Directives.Add(directive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to build dynamic entities for user {UserId}", scopedUserId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the session's plugin user id (cheap config lookups, needed for the
    /// builder's cache key) plus a LAZY library-scope resolver: the resolution
    /// itself (<see cref="LibraryFilter.ResolveForUser"/>) is deferred to the
    /// builder's cache-miss branch so output-cache hits pay no scope resolution
    /// (code-review round 2 item 4). When invoked, the resolver returns the FULLY
    /// RESOLVED scope: raw collection-folder ids never escape it (JF-456). A null
    /// resolver result = unrestricted user.
    /// </summary>
    private (Guid UserId, Func<Guid[]?>? ResolveScope, string? BoundLibraryId) ResolveUserWithLibraryScope(RequestContext context)
    {
        // Voice-based identification takes priority (multi-user households)
        string? personId = context.AlexaContext?.System?.Person?.PersonId;
        if (!string.IsNullOrEmpty(personId))
        {
            Entities.User? user = _config.GetUserByPersonId(personId);
            if (user != null)
            {
                return (user.Id, () => LibraryFilter.ResolveForUser(user, _libraryManager, _logger), null);
            }
            // PersonId present but unmapped: the room binding still applies below.
        }

        // Account linking fallback
        string? accessToken = context.AlexaContext?.System?.User?.AccessToken;
        if (Guid.TryParse(accessToken, out Guid userId))
        {
            Entities.User? user = _config.GetUserById(userId);
            if (user == null)
            {
                // Parsed-but-stale token id (config wiped, the JF-588 shape): the
                // funnel answers this same request with user-not-found, so entity
                // values would leak every library's names onto a device the admin
                // may have bound (fail-open, review round 2). Mirror the funnel's
                // verdict: no user, no entity refresh.
                _logger.LogDebug("Dynamic entities: token id {UserId} resolves to no plugin user; skipping entity refresh", userId);
                return (Guid.Empty, null, null);
            }

            // JF-327: dynamic entity values must reflect the device's bound library.
            // A person id that reached this arm is unmapped (not a recognized
            // profile), so the device binding correctly applies. The binding's
            // library id also becomes the output-cache scope dimension: without it
            // a bound device can be served whatever scope was first cached for
            // this user (review round 2).
            string? boundLibraryId = _config.GetDeviceLibraryBinding(
                context.AlexaContext?.System?.Device?.DeviceID)?.LibraryId;
            user = Alexa.Util.DeviceLibraryBindingResolver.Apply(context.AlexaContext, user, _config, _logger);
            return (userId, () => LibraryFilter.ResolveForUser(user, _libraryManager, _logger), boundLibraryId);
        }

        _logger.LogDebug("Could not resolve Jellyfin user ID for dynamic entities");
        return (Guid.Empty, null, null);
    }

    /// <summary>
    /// Built-in playback-control intents. These carry no slot to resolve and must
    /// never trigger a whole-library dynamic-entity refresh.
    /// </summary>
    private static readonly HashSet<string> PlaybackControlIntents = new(StringComparer.Ordinal)
    {
        "AMAZON.ShuffleOnIntent",
        "AMAZON.ShuffleOffIntent",
        "AMAZON.NextIntent",
        "AMAZON.PreviousIntent",
        "AMAZON.LoopOnIntent",
        "AMAZON.LoopOffIntent",
        "AMAZON.RepeatOnIntent",
        "AMAZON.RepeatOffIntent",
        "AMAZON.PauseIntent",
        "AMAZON.ResumeIntent",
        "AMAZON.StopIntent",
        "AMAZON.CancelIntent",
        "AMAZON.StartOverIntent"
    };

    private static bool IsPlaybackControlIntent(string? intentName) =>
        intentName != null && PlaybackControlIntents.Contains(intentName);
}
