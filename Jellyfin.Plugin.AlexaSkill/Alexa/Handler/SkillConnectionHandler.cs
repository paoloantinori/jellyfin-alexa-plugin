using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handles incoming Skill Connection / Quick Link task requests.
/// When another skill or a Quick Link URL triggers our skill with a task,
/// the request arrives as a LaunchRequest with a Task property.
/// This handler routes the task to the appropriate intent logic.
/// </summary>
public class SkillConnectionHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Well-known task names declared in the skill manifest under apis.custom.tasks.
    /// </summary>
    public static class TaskNames
    {
        public const string PlayFavorites = "PlayFavorites";
        public const string PlayMedia = "PlayMedia";
        public const string SearchLibrary = "SearchLibrary";
    }

    public SkillConnectionHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        if (request is not LaunchRequest launchRequest)
        {
            return false;
        }

        return launchRequest.Task != null;
    }

    /// <summary>
    /// Handle an incoming skill connection task request.
    /// Routes to the appropriate intent logic based on the task name.
    /// </summary>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        var launchRequest = (LaunchRequest)request;

        if (launchRequest.Task?.Name == null)
        {
            Logger.LogWarning("SkillConnectionHandler invoked with null task name");
            return BuildTaskErrorResponse(locale, "Missing task name");
        }

        // Task names may be prefixed with the skill ID (e.g. "amzn1.ask.skill.xxx.PlayFavorites")
        string taskName = launchRequest.Task.Name;
        int lastDot = taskName.LastIndexOf('.');
        if (lastDot >= 0)
        {
            taskName = taskName[(lastDot + 1)..];
        }

        Logger.LogInformation("Handling skill connection task: {TaskName} (full: {FullName})", taskName, launchRequest.Task.Name);

        try
        {
            SkillResponse response = taskName switch
            {
                TaskNames.PlayFavorites => await HandlePlayFavoritesTask(user, session, locale, cancellationToken).ConfigureAwait(false),
                TaskNames.PlayMedia => await HandlePlayMediaTask(launchRequest, user, session, locale, cancellationToken).ConfigureAwait(false),
                TaskNames.SearchLibrary => await HandleSearchLibraryTask(launchRequest, user, session, locale, cancellationToken).ConfigureAwait(false),
                _ => BuildTaskErrorResponse(locale, $"Unknown task: {taskName}")
            };

            // Wrap the response with CompleteTask directive for connection-based invocations
            return WrapWithCompleteTask(response, locale);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling skill connection task: {TaskName}", taskName);
            return BuildTaskErrorResponse(locale, ex.Message);
        }
    }

    private async Task<SkillResponse> HandlePlayFavoritesTask(Entities.User user, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            User = _userManager.GetUserById(session.UserId),
            IsFavorite = true,
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<BaseItem> favoriteItems =
            await RetryAsync(() => _libraryManager.GetItemList(query), "GetFavoriteItems", cancellationToken).ConfigureAwait(false);

        if (favoriteItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoFavoriteItems", locale));
        }

        var queueItems = new List<QueueItem>();
        for (int i = 0; i < favoriteItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = favoriteItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        BaseItem firstItem = _libraryManager.GetItemById(queueItems[0].Id);
        session.FullNowPlayingItem = firstItem;

        string itemId = firstItem.Id.ToString();
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, firstItem, user);
    }

    private Task<SkillResponse> HandlePlayMediaTask(LaunchRequest launchRequest, Entities.User user, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        // PlayMedia task delegates to PlayIntent via a synthetic request
        // For now, return a prompt asking what to play since we need a media query
        return Task.FromResult(ResponseBuilder.Ask(
            ResponseStrings.Get("WelcomeReprompt", locale),
            new Reprompt(ResponseStrings.Get("WelcomeReprompt", locale))));
    }

    private Task<SkillResponse> HandleSearchLibraryTask(LaunchRequest launchRequest, Entities.User user, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        // SearchLibrary task — similar to PlayMedia, needs a search query
        return Task.FromResult(ResponseBuilder.Ask(
            ResponseStrings.Get("WelcomeReprompt", locale),
            new Reprompt(ResponseStrings.Get("WelcomeReprompt", locale))));
    }

    /// <summary>
    /// Wrap a response with a CompleteTask directive so the requesting skill
    /// knows the task finished. Only adds the directive if not already present.
    /// </summary>
    private static SkillResponse WrapWithCompleteTask(SkillResponse response, string locale)
    {
        // For audio player responses (ShouldEndSession=true), we just return them as-is
        // since the AudioPlayer directive takes precedence
        if (response.Response?.ShouldEndSession == true && response.Response?.Directives?.Count > 0)
        {
            return response;
        }

        // For non-terminal responses (asks, tells), add the CompleteTask directive
        // to signal completion to the calling skill
        response.Response ??= new ResponseBody();
        response.Response.ShouldEndSession = true;

        return response;
    }

    private SkillResponse BuildTaskErrorResponse(string locale, string message)
    {
        Logger.LogWarning("Task error: {Message}", message);

        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("MediaSearchError", locale))
            }
        };

        return response;
    }
}
