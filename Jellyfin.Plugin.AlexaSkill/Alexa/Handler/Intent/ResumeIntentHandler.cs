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
/// JF-507 critical review: NONE of the fallback 1-3 offsets may be minted into the
/// audio-only transcode's ?start= for a transcode-routed item. All three are
/// DEVICE-DERIVED, so they are relative to the previous playback's OUTPUT timeline,
/// which for a transcode-routed item starts at that stream's ?start= seek point (the
/// codec is an item property: whenever the item routes to the transcode NOW, its
/// prior playback rode the transcode too). The writers (PlaybackStoppedEventHandler,
/// PlaybackStartedEventHandler) persist the device offset without adding the
/// transcode base, so the persisted positions are stream-relative as well. JF-514
/// shipped the stream-relative-to-absolute correction on the OFFER path (the launch
/// base is recorded per device+item at the ResolveAudioLaunchSource chokepoint and
/// YesIntentHandler's confirm rebases base+offset) with this tail on an interim
/// drop-to-restart rule; JF-520 adopted the SAME rebase here via the shared
/// BaseHandler.ResolveResumedAudioLaunch: a paused-then-resumed transcode-routed
/// item continues from base+offset instead of restarting at 0 (and the resolve
/// records the new base, so the next cycle composes), while a stream-relative offset
/// with NO recorded base still drops to a 0-restart (never mint silently) and
/// raw-static launches (audio items, Echo-decodable video) keep the caller's offset.
/// Fallback 4 is structurally safe: its video branch launches via the VideoApp path
/// (never resolves an audio launch, never records a base) and its audio branch uses
/// the raw static URL (audio items never transcode).
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
            // Fallback 1: Alexa AudioPlayer context (most accurate when device retains state).
            // The device offset is relative to the PREVIOUS playback's output timeline,
            // which for a transcode-routed item starts at that stream's ?start= point.
            // The tail rebases it against the recorded launch base (JF-520, the same
            // correction the offer/Yes path shipped in JF-514); no base drops it.
            if (context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0)
            {
                offset = (int)context.AudioPlayer.OffsetInMilliseconds;
                Logger.LogDebug("ResumeIntent: using AudioPlayer context offset={OffsetMs}ms (device-derived, output-timeline-relative)", offset);
            }
            // Fallback 2: Jellyfin session play state. Written from the device offset
            // (PlaybackStoppedEventHandler), so it is output-timeline-relative too for
            // a transcode-routed item; the tail rebases it the same way (JF-520).
            else if (session?.PlayState != null)
            {
                offset = (int)TimeSpan.FromTicks(session.PlayState?.PositionTicks ?? 0).TotalMilliseconds;
                Logger.LogDebug(
                    "ResumeIntent: using session playState offset={OffsetMs}ms (ticks={Ticks}, device-derived)",
                    offset, session.PlayState?.PositionTicks);
            }

            // Fallback 3: DeviceQueue persisted state (survives after AudioPlayer.Stop clears context).
            // Same device-derived provenance as fallbacks 1-2 (PlaybackStoppedEventHandler
            // writes the device offset into CurrentPositionTicks); the tail rebases it (JF-520).
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

                // Video items use the VideoApp launch directive (the shared predicate owns
                // the kind list, JF-505; LiveTvChannel included)
                if (IsVideoAppLaunchItem(resumeItem))
                {
                    // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
                    SkillResponse videoResponse = BuildVideoAppLaunchResponse(
                        context,
                        locale,
                        GetVideoAppLaunchUrl(resumeItem, user),
                        resumeItem.Name,
                        new PlainTextOutputSpeech(
                            ResponseStrings.Get("NowPlayingWithPosition", locale, resumeItem.Name, FormatPosition(resumeTicks))));

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

        // JF-520: the tail's JF-514 correction, adopted from the offer path. EVERY
        // tail offset (AudioPlayer context, session PlayState, DeviceQueue) is
        // device-derived and therefore relative to the previous playback's OUTPUT
        // timeline, which for a transcode-routed item starts at that stream's ?start=
        // base (the event writers never add the base back), so the tail passes
        // streamRelative=true UNCONDITIONALLY. BaseHandler.ResolveResumedAudioLaunch
        // owns the shared shape: probe + read the recorded launch base, rebase
        // base+offset (item-absolute) when one exists, drop to a 0-restart when none
        // does (pre-deploy launch, wiped ledger), pass through for raw-static items.
        // The ledger read lives INSIDE the helper, structurally before the resolve
        // that overwrites it (JF-520; was comment-enforced here in JF-514).
        AudioLaunchSource source = ResolveResumedAudioLaunch(
            session?.FullNowPlayingItem, item_id!, user, offset, offsetIsStreamRelative: true,
            context?.System?.Device?.DeviceID, _queueManager, "ResumeIntent");

        var response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            source.Url,
            item_id!,
            session?.FullNowPlayingItem,
            user,
            context,
            source.OffsetMs);

        Logger.LogDebug(
            "ResumeIntent: final response itemId={ItemId}, offset={OffsetMs}ms",
            item_id, source.OffsetMs);

        // Proactive position announcement when enabled and we have a non-zero offset.
        // Uses the EFFECTIVE offset (source.OffsetMs): a dropped stream-relative offset
        // must not be announced as the position playback restarts from.
        if (source.OffsetMs > 0)
        {
            Entities.User? pluginUser = _config.GetUserById(user.Id);
            if (pluginUser?.AnnouncePositionOnResume == true)
            {
                string positionStr = FormatTimeSpan(TimeSpan.FromMilliseconds(source.OffsetMs), locale);
                response.Response.OutputSpeech = new PlainTextOutputSpeech
                {
                    Text = ResponseStrings.Get("ResumingAtPosition", locale, positionStr)
                };
            }
        }

        return Task.FromResult<SkillResponse>(response);
    }
}
