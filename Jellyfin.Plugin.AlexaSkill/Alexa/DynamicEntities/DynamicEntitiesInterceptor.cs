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
    private readonly ILogger<DynamicEntitiesInterceptor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicEntitiesInterceptor"/> class.
    /// </summary>
    /// <param name="builder">The dynamic entity builder.</param>
    /// <param name="config">The plugin configuration for user resolution.</param>
    /// <param name="logger">Logger instance.</param>
    public DynamicEntitiesInterceptor(
        DynamicEntityBuilder builder,
        PluginConfiguration config,
        ILogger<DynamicEntitiesInterceptor> logger)
    {
        _builder = builder;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (context.Response?.Response == null)
        {
            return;
        }

        // AudioPlayer.Play responses must not include other directives.
        if (context.Response.Response.Directives?.Any(d => d is AudioPlayerPlayDirective) == true)
        {
            return;
        }

        bool isNewSession = context.SkillRequest is LaunchRequest
            || (context.AlexaSession?.New ?? false);

        string intentName = context.IntentName;

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
                return;
            }
        }

        var (jellyfinUserId, allowedLibraryIds) = ResolveUserWithLibraries(context);
        if (jellyfinUserId == Guid.Empty)
        {
            return;
        }

        try
        {
            DynamicEntitiesDirective? directive = await Task.Run(
                () => _builder.Build(jellyfinUserId, context.Locale, allowedLibraryIds, includeSeries, includeAudiobooks, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (directive == null)
            {
                return;
            }

            context.Response.Response.Directives ??= new List<global::Alexa.NET.Response.IDirective>();
            context.Response.Response.Directives.Add(directive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to build dynamic entities for user {UserId}", jellyfinUserId);
        }
    }

    private (Guid UserId, Guid[]? AllowedLibraryIds) ResolveUserWithLibraries(RequestContext context)
    {
        // Voice-based identification takes priority (multi-user households)
        string? personId = context.AlexaContext?.System?.Person?.PersonId;
        if (!string.IsNullOrEmpty(personId))
        {
            Entities.User? user = _config.GetUserByPersonId(personId);
            if (user != null)
            {
                return (user.Id, LibraryFilter.GetAllowedLibraryIds(user));
            }
        }

        // Account linking fallback
        string? accessToken = context.AlexaContext?.System?.User?.AccessToken;
        if (Guid.TryParse(accessToken, out Guid userId))
        {
            Entities.User? user = _config.GetUserById(userId);
            return (userId, LibraryFilter.GetAllowedLibraryIds(user));
        }

        _logger.LogDebug("Could not resolve Jellyfin user ID for dynamic entities");
        return (Guid.Empty, null);
    }
}
