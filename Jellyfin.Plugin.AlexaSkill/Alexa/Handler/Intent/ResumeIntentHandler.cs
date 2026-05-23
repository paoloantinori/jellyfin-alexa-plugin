using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.ResumeIntent intents and resume directive.
/// Restores playback from the last known position using a four-tier fallback:
/// 1. Alexa AudioPlayer context (most accurate when device retains state)
/// 2. Jellyfin session play state
/// 3. DeviceQueue persisted state (survives device state loss after pause)
/// 4. Jellyfin server-side progress (queries last played item with resume position)
/// </summary>
public class ResumeIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResumeIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for pause/resume state.</param>
    public ResumeIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonResume, System.StringComparison.Ordinal)) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Play);
    }

    /// <summary>
    /// Resume paused media playback.
    /// Uses four-tier fallback for position recovery: Alexa context, Jellyfin session, DeviceQueue, server-side progress.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Skill response with AudioPlayer directive, or error message.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        var intentReq = request as IntentRequest;
        Logger.LogDebug("ResumeIntent: entered, locale={Locale}, playerActivity={Activity}", locale, context.AudioPlayer?.PlayerActivity);

        if (string.Equals(context.AudioPlayer?.PlayerActivity, "PLAYING", StringComparison.Ordinal))
        {
            Logger.LogDebug("ResumeIntent: already PLAYING, returning empty");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        }

        // Prefer AudioPlayer token (survives session cleanup after PlaybackStopped),
        // fall back to session's now-playing item ID
        string? item_id = context.AudioPlayer?.Token
            ?? session?.FullNowPlayingItem?.Id.ToString();

        int offset = 0;

        Logger.LogDebug(
            "ResumeIntent: device={DeviceId}, audioPlayer token={Token} activity={Activity} offset={AudioOffset}ms, session itemId={SessionItem}",
            context.System.Device.DeviceID,
            context.AudioPlayer?.Token,
            context.AudioPlayer?.PlayerActivity,
            context.AudioPlayer?.OffsetInMilliseconds,
            session?.FullNowPlayingItem?.Id.ToString());

        if (!string.IsNullOrEmpty(item_id))
        {
            // Fallback 1: Alexa AudioPlayer context (most accurate when device retains state)
            if (context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0)
            {
                offset = (int)context.AudioPlayer.OffsetInMilliseconds;
                Logger.LogDebug("ResumeIntent: using AudioPlayer context offset={OffsetMs}ms", offset);
            }
            // Fallback 2: Jellyfin session play state
            else if (session?.PlayState != null)
            {
                offset = (int)TimeSpan.FromTicks(session.PlayState?.PositionTicks ?? 0).TotalMilliseconds;
                Logger.LogDebug(
                    "ResumeIntent: using session playState offset={OffsetMs}ms (ticks={Ticks})",
                    offset, session.PlayState?.PositionTicks);
            }

            // Fallback 3: DeviceQueue persisted state (survives after AudioPlayer.Stop clears context)
            if (offset == 0 && _queueManager != null)
            {
                var queue = _queueManager.GetOrCreateQueue(context.System.Device.DeviceID);
                if (!string.IsNullOrEmpty(queue.CurrentItemId) && queue.CurrentPositionTicks > 0)
                {
                    if (string.IsNullOrEmpty(context.AudioPlayer?.Token) ||
                        string.Equals(context.AudioPlayer.Token, queue.CurrentItemId, StringComparison.Ordinal))
                    {
                        item_id = queue.CurrentItemId;
                        offset = (int)TimeSpan.FromTicks(queue.CurrentPositionTicks).TotalMilliseconds;
                        Logger.LogInformation(
                            "ResumeIntent: using DeviceQueue fallback for device {DeviceId}: item={ItemId}, offset={OffsetMs}ms",
                            context.System.Device.DeviceID, item_id, offset);
                    }
                }
            }
        }

        // Fallback 4: Jellyfin server-side progress (queries last played item with resume position)
        if (string.IsNullOrEmpty(item_id))
        {
            Logger.LogDebug("ResumeIntent: no item_id from context/session, trying server-side progress fallback");
            if (session == null)
            {
                return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
            }

            var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
            if (userError != null)
            {
                return Task.FromResult<SkillResponse>(userError);
            }

            Entities.User pluginUser = _config.GetUserById(user.Id) ?? user;
            BaseItemKind[] contentTypes = FilterByContentAccess(new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.AudioBook });

            var (resumeItem, resumeTicks) = FindLastPlayedItemWithProgress(
                jellyfinUser!,
                _libraryManager,
                _userDataManager,
                pluginUser,
                contentTypes,
                Logger);

            if (resumeItem != null)
            {
                Logger.LogInformation(
                    "ResumeIntent: using server-side progress fallback: item={ItemName} ({ItemId}), position={Position}",
                    resumeItem.Name, resumeItem.Id, FormatPosition(resumeTicks));

                item_id = resumeItem.Id.ToString();
                offset = (int)TimeSpan.FromTicks(resumeTicks).TotalMilliseconds;

                // Video items (Movie/Episode) use VideoApp launch directive
                if (resumeItem is MediaBrowser.Controller.Entities.Movies.Movie
                    or MediaBrowser.Controller.Entities.TV.Episode)
                {
                    SkillResponse videoResponse = new SkillResponse
                    {
                        Version = "1.0",
                        Response = new ResponseBody
                        {
                                OutputSpeech = new PlainTextOutputSpeech(
                                ResponseStrings.Get("NowPlayingWithPosition", locale, resumeItem.Name, FormatPosition(resumeTicks))),
                            Directives = new List<IDirective>
                            {
                                new Directive.VideoAppLaunchDirective
                                {
                                    VideoItem = new Directive.VideoItem
                                    {
                                        Source = GetVideoStreamUrl(item_id, user),
                                        Metadata = new Directive.VideoItemMetadata { Title = resumeItem.Name }
                                    }
                                }
                            }
                        }
                    };

                    return Task.FromResult<SkillResponse>(videoResponse);
                }

                // Audio/AudioBook items use AudioPlayer response with offset
                var audioResponse = BuildAudioPlayerResponse(
                    PlayBehavior.ReplaceAll,
                    GetStreamUrl(item_id, user),
                    item_id,
                    resumeItem,
                    user,
                    context,
                    offset);

                // Announce resume position if enabled
                if (offset > 0 && pluginUser.AnnouncePositionOnResume)
                {
                    string positionStr = FormatTimeSpan(TimeSpan.FromMilliseconds(offset), locale);
                    audioResponse.Response.OutputSpeech = new PlainTextOutputSpeech
                    {
                        Text = ResponseStrings.Get("ResumingAtPosition", locale, positionStr)
                    };
                }

                return Task.FromResult<SkillResponse>(audioResponse);
            }

            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        var response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            GetStreamUrl(item_id!, user),
            item_id!,
            session?.FullNowPlayingItem,
            user,
            context,
            offset);

        Logger.LogDebug(
            "ResumeIntent: final response itemId={ItemId}, offset={OffsetMs}ms",
            item_id, offset);

        // Proactive position announcement when enabled and we have a non-zero offset
        if (offset > 0)
        {
            Entities.User? pluginUser = _config.GetUserById(user.Id);
            if (pluginUser?.AnnouncePositionOnResume == true)
            {
                string positionStr = FormatTimeSpan(TimeSpan.FromMilliseconds(offset), locale);
                response.Response.OutputSpeech = new PlainTextOutputSpeech
                {
                    Text = ResponseStrings.Get("ResumingAtPosition", locale, positionStr)
                };
            }
        }

        return Task.FromResult<SkillResponse>(response);
    }
}
