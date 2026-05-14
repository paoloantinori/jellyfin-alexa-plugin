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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for LaunchRequest intents.
/// When the skill is re-launched while audio was previously active, detects the
/// prior playback state via the AudioPlayer context and asks the user whether
/// to resume, using a Yes/No confirmation flow.
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
    /// When AudioPlayer context indicates prior playback, offer resume confirmation.
    /// </summary>
    /// <param name="request">The skill intent request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A play directive or a question what should be played.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // Check if audio was playing before this re-launch (AudioPlayer context carries the token/offset)
        if (!string.IsNullOrEmpty(context.AudioPlayer?.Token))
        {
            return await HandleResumeOfferAsync(request, context, user, session, locale, cancellationToken).ConfigureAwait(false);
        }

        // check if we have any media in the queue (legacy Jellyfin session-based resume)
        if (session.NowPlayingQueue.Count > 0)
        {
            return HandleSessionQueueResume(request, user, session);
        }

        // No prior playback — show welcome
        return await BuildWelcomeResponseAsync(context, locale, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handle the case where AudioPlayer context indicates prior playback.
    /// Looks up the item, offers a resume confirmation prompt.
    /// </summary>
    private async Task<SkillResponse> HandleResumeOfferAsync(
        Request request, Context context, Entities.User user, SessionInfo session,
        string locale, CancellationToken cancellationToken)
    {
        string itemId = context.AudioPlayer!.Token!;
        long offsetMs = context.AudioPlayer.OffsetInMilliseconds;

        // Look up the item for its display name
        BaseItem? item = null;
        if (Guid.TryParse(itemId, out Guid itemGuid))
        {
            item = await RetryAsync(
                () => _libraryManager.GetItemById(itemGuid),
                "LaunchResumeLookup",
                cancellationToken).ConfigureAwait(false);
        }

        string title = item?.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        string? resumeSsml = GetSsml("ResumePromptSsml", locale, EscapeXml(title));
        string repromptText = ResponseStrings.Get("ResumeReprompt", locale);

        SkillResponse response;
        if (resumeSsml != null)
        {
            response = AskSsml(resumeSsml, repromptText);
        }
        else
        {
            string prompt = ResponseStrings.Get("ResumePrompt", locale, title);
            response = ResponseBuilder.Ask(prompt, new Reprompt(repromptText));
        }

        // Store resume state in session attributes using proper DTO for serialization
        var resumeState = new ResumeHelper.ResumeState
        {
            ItemId = itemId,
            OffsetMs = offsetMs
        };

        response.SessionAttributes = new Dictionary<string, object>
        {
            ["resume_state"] = JsonConvert.SerializeObject(resumeState)
        };

        return response;
    }

    /// <summary>
    /// Handle the legacy Jellyfin session-based queue resume (no confirmation prompt).
    /// </summary>
    private SkillResponse HandleSessionQueueResume(Request request, Entities.User user, SessionInfo session)
    {
        // check if something is currently playing which we can resume
        if (session.FullNowPlayingItem != null)
        {
            string item_id = session.FullNowPlayingItem.Id.ToString();

            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, session.FullNowPlayingItem, user, null);
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

            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, item, user, null);
        }
    }

    /// <summary>
    /// Build the welcome response with optional personalization.
    /// </summary>
    private async Task<SkillResponse> BuildWelcomeResponseAsync(Context context, string locale, CancellationToken cancellationToken)
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
}
