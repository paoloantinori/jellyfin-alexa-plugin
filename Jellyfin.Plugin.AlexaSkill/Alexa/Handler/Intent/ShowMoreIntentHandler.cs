using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handles the ShowMoreIntent to display the next page of list results.
/// Reads pagination state from session attributes and resolves items by ID.
/// </summary>
public class ShowMoreIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowMoreIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public ShowMoreIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is IntentRequest intentRequest
            && string.Equals(intentRequest.Intent.Name, IntentNames.ShowMore, StringComparison.Ordinal);
    }

    /// <summary>
    /// Handle without session attributes - no pagination state available.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        Logger.LogDebug("ShowMore: no session attributes available");
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("ShowMoreNoSession", GetLocale(request))));
    }

    /// <summary>
    /// Handle with session attributes - read pagination state and return next page.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        var paginationState = ListPaginationHelper.ReadState(sessionAttributes);
        if (paginationState == null)
        {
            Logger.LogDebug("ShowMore: no pagination state in session attributes");
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("ShowMoreNoSession", locale)));
        }

        Logger.LogDebug("ShowMore: continuing pagination type={ListType}, offset={CurrentOffset}, totalItems={ItemCount}", paginationState.Type, paginationState.CurrentOffset, paginationState.ItemIds.Length);
        return Task.FromResult(ListPaginationHelper.BuildNextPageResponse(_libraryManager, paginationState, locale));
    }
}
