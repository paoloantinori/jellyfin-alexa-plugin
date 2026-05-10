using System;
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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for LaunchRequest intents.
/// </summary>
public class LaunchRequestHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly CustomerProfileService _profileService;

    /// <summary>
    /// Initializes a new instance of the <see cref="LaunchRequestHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public LaunchRequestHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _profileService = new CustomerProfileService(loggerFactory.CreateLogger<CustomerProfileService>());
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        // Task-bearing LaunchRequests are handled by SkillConnectionHandler
        return request is LaunchRequest { Task: null };
    }

    /// <summary>
    /// Resume any currently playing media or ask the user to say some media name to play.
    /// </summary>
    /// <param name="request">The skill intent request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>A play directive or a question what should be played.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // check if we have any media in the queue
        if (session.NowPlayingQueue.Count == 0)
        {
            string? givenName = await _profileService.GetGivenNameAsync(context, cancellationToken).ConfigureAwait(false);

            string welcomeText = !string.IsNullOrEmpty(givenName)
                ? ResponseStrings.Get("WelcomePersonalized", locale, givenName!)
                : ResponseStrings.Get("Welcome", locale);

            string welcomeSsmlKey = !string.IsNullOrEmpty(givenName) ? "WelcomePersonalizedSsml" : "WelcomeSsml";
            string? welcomeSsml = !string.IsNullOrEmpty(givenName)
                ? string.Format(CultureInfo.InvariantCulture, ResponseStrings.Get(welcomeSsmlKey, locale), EscapeXml(givenName!))
                : GetSsml("WelcomeSsml", locale);

            string? repromptSsml = GetSsml("WelcomeRepromptSsml", locale);

            if (welcomeSsml != null && repromptSsml != null)
            {
                return AskSsml(welcomeSsml, repromptSsml);
            }

            return ResponseBuilder.Ask(
                welcomeText,
                new Reprompt(ResponseStrings.Get("WelcomeReprompt", locale)));
        }

        // check if something is currently playing which we can resume
        if (session.FullNowPlayingItem != null)
        {
            string item_id = session.FullNowPlayingItem.Id.ToString();

            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, session.FullNowPlayingItem, user);
        }
        else
        {
            // resume the first item in the queue
            BaseItem? item = _libraryManager.GetItemById(session.NowPlayingQueue[0].Id);
            if (item == null)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", GetLocale(request)));
            }

            string item_id = item.Id.ToString();
            session.FullNowPlayingItem = item;

            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, item, user);
        }
    }
}
