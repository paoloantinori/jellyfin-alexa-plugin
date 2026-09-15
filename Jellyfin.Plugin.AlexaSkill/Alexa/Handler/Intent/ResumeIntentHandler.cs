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
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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
/// JF-507 critical review: no fallback 1-3 offset may be minted into the audio-only
/// transcode's ?start= as if it were item-absolute when it is not. JF-522 re-scoped
/// the provenance split: fallback 1 (the AudioPlayer context offset) is written by
/// AMAZON and counts the previous playback's OUTPUT timeline, stream-relative by
/// platform contract, so it still goes through the shared rebase in
/// BaseHandler.ResolveResumedAudioLaunch (JF-514/JF-520: base+offset against the
/// launch-scoped base, drop to a 0-restart when none is recorded); fallbacks 2-3
/// (session PlayState, DeviceQueue.CurrentPositionTicks) are persisted ITEM-ABSOLUTE
/// by the event writers since the JF-522 writer fix (the stop event composes the
/// stream's launch base at write time), so they pass through unchanged. Values a
/// rolling deploy may still carry from the pre-JF-522 raw regime mint early, never
/// past the true position, and heal on the first post-deploy stop. JF-521: a
/// stream-relative composition that reaches or exceeds the item's runtime (when
/// known) is never minted; the raw offset wins.
/// Fallback 4 is structurally safe: its video branch launches via the VideoApp path
/// (never resolves an audio launch, never records a base) and its audio branch uses
/// the raw static URL (audio items never transcode). The NativeControlsForBooks book
/// branches (JF-563: fallback-4 AND the tail, via one shared helper) have the same
/// property: the sliced VideoApp playlist launch never resolves an audio launch or
/// records a base, the fallback ticks they mint are only ever item-absolute (a book
/// never transcodes, so its raw-static output timeline IS its item timeline), and a
/// screenless device degrades to the raw-static AudioPlayer shape.
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
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        var intentReq = request as IntentRequest;
        Logger.LogDebug("ResumeIntent: entered, locale={Locale}, playerActivity={Activity}", locale, context.AudioPlayer?.PlayerActivity);

        if (string.Equals(context.AudioPlayer?.PlayerActivity, "PLAYING", StringComparison.Ordinal))
        {
            Logger.LogDebug("ResumeIntent: already PLAYING, returning empty");
            return ResponseBuilder.Empty();
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

        // JF-522 per-fallback provenance: only fallback 1 (the AudioPlayer context
        // offset, written by AMAZON) is stream-relative by platform contract. The
        // plugin-written fallbacks 2-3 (session PlayState, DeviceQueue position) are
        // persisted ITEM-ABSOLUTE since the JF-522 writer fix, so they pass through
        // unchanged; the pre-JF-522 values a rolling deploy may still hold are read
        // conservatively (they mint early, never past the true position).
        bool offsetIsStreamRelative = false;

        if (!string.IsNullOrEmpty(item_id))
        {
            // Fallback 1: Alexa AudioPlayer context (most accurate when device retains state).
            // The device offset is relative to the PREVIOUS playback's output timeline,
            // which for a transcode-routed item starts at that stream's ?start= point.
            // The tail rebases it against the launch-scoped base (JF-520/JF-522, the same
            // correction the offer/Yes path shipped in JF-514); no base drops it.
            if (context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0)
            {
                offset = (int)context.AudioPlayer.OffsetInMilliseconds;
                offsetIsStreamRelative = true;
                Logger.LogDebug("ResumeIntent: using AudioPlayer context offset={OffsetMs}ms (device-derived, output-timeline-relative)", offset);
            }
            // Fallback 2: Jellyfin session play state. Persisted item-absolute by the
            // event writers since JF-522 (launch base composed at write time).
            else if (session?.PlayState != null)
            {
                offset = (int)TimeSpan.FromTicks(session.PlayState?.PositionTicks ?? 0).TotalMilliseconds;
                Logger.LogDebug(
                    "ResumeIntent: using session playState offset={OffsetMs}ms (ticks={Ticks}, item-absolute since JF-522)",
                    offset, session.PlayState?.PositionTicks);
            }

            // Fallback 3: DeviceQueue persisted state (survives after AudioPlayer.Stop clears context).
            // Item-absolute since JF-522 (the stop event composes the launch base into
            // CurrentPositionTicks).
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
                return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale));
            }

            var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
            if (userError != null)
            {
                return userError;
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
                    resumeItem.Name, resumeItem.Id, ResumeMath.FormatPosition(resumeTicks));

                item_id = resumeItem.Id.ToString();
                offset = (int)TimeSpan.FromTicks(resumeTicks).TotalMilliseconds;

                // NativeControlsForBooks (JF-563): an audiobook resumes through the same
                // VideoApp HLS entry PlayBook uses (the sliced ?start= playlist), not the
                // flat /Audio stream that loses the seek bar the flag exists to provide.
                // Gated at the caller, NOT in IsVideoAppLaunchItem: the predicate is the
                // durable movie-shaped kind list whose other callers (the resume offer's
                // screenless gate, Repeat's displacement classification) must not treat a
                // book as video, and the flag is mutable config (the JF-499 W1 split).
                SkillResponse? bookResponse = await TryBuildNativeControlsBookResumeAsync(
                    resumeItem, resumeTicks, user, context, request, locale).ConfigureAwait(false);
                if (bookResponse != null)
                {
                    return bookResponse;
                }

                // Video items use the VideoApp launch directive (the shared predicate owns
                // the kind list, JF-505; LiveTvChannel included)
                if (IsVideoAppLaunchItem(resumeItem))
                {
                    // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
                    // JF-501: the announce is spoken progressively (directive-only final response).
                    return await BuildVideoAppLaunchResponseAsync(
                        context,
                        request,
                        locale,
                        GetVideoAppLaunchUrl(resumeItem, user),
                        resumeItem.Name,
                        new PlainTextOutputSpeech(
                            ResponseStrings.Get("NowPlayingWithPosition", locale, resumeItem.Name, ResumeMath.FormatPosition(resumeTicks)))).ConfigureAwait(false);
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
                    string positionStr = ResumeMath.FormatTimeSpan(TimeSpan.FromMilliseconds(offset), locale);
                    audioResponse.Response.OutputSpeech = new PlainTextOutputSpeech
                    {
                        Text = ResponseStrings.Get("ResumingAtPosition", locale, positionStr)
                    };
                }

                return audioResponse;
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale));
        }

        // JF-563: the same book routing for a session-held book. A VideoApp launch never
        // sets the AudioPlayer token, so a book resume typically reaches this tail via
        // FullNowPlayingItem; without this branch it would flat-launch and lose the
        // sliced-resume seek bar. The token guard keeps a DISPLACED token authoritative
        // (a different item playing), mirroring the DeviceQueue fallback's discipline.
        if (string.IsNullOrEmpty(context.AudioPlayer?.Token)
            || string.Equals(context.AudioPlayer.Token, session?.FullNowPlayingItem?.Id.ToString(), StringComparison.Ordinal))
        {
            SkillResponse? bookResponse = await TryBuildNativeControlsBookResumeAsync(
                session?.FullNowPlayingItem, offset, user, context, request, locale).ConfigureAwait(false);
            if (bookResponse != null)
            {
                return bookResponse;
            }
        }

        // The tail's JF-514 correction, adopted from the offer path (JF-520) and
        // re-scoped by JF-522: the AudioPlayer-context offset (Amazon-written) stays
        // stream-relative forever, so the tail rebases ONLY that one against the
        // launch-scoped base. BaseHandler.ResolveResumedAudioLaunch owns the shared
        // shape: probe + read the active launch base, rebase base+offset
        // (item-absolute) when one exists, drop to a 0-restart when none does
        // (pre-deploy launch, wiped scope), pass through for raw-static items and
        // item-absolute offsets. The base read lives INSIDE the helper, structurally
        // before the resolve/directive that overwrites it (JF-520; was
        // comment-enforced here in JF-514).
        AudioLaunchSource source = ResolveResumedAudioLaunch(
            session?.FullNowPlayingItem, item_id!, user, offset, offsetIsStreamRelative,
            context?.System?.Device?.DeviceID, _queueManager, "ResumeIntent");

        var response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            source,
            item_id!,
            session?.FullNowPlayingItem,
            user,
            context,
            queueManager: _queueManager);

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
                string positionStr = ResumeMath.FormatTimeSpan(TimeSpan.FromMilliseconds(source.OffsetMs), locale);
                response.Response.OutputSpeech = new PlainTextOutputSpeech
                {
                    Text = ResponseStrings.Get("ResumingAtPosition", locale, positionStr)
                };
            }
        }

        return response;
    }

    /// <summary>
    /// JF-563: the NativeControlsForBooks resume of an audiobook, shared by fallback-4
    /// (server-side progress) and the tail (a session-held book; a VideoApp launch never
    /// sets the AudioPlayer token, so a book resume usually arrives via
    /// FullNowPlayingItem). Rides the same sliced VideoApp HLS playlist PlayBook uses,
    /// tracker position first, the caller's fallback ticks when the tracker is cold.
    /// Returns null when the item is not a book or the flag is off, so the caller falls
    /// through to its normal path. On a screenless device both builders degrade to the
    /// AudioPlayer flat resume, so the flag cannot break the play.
    /// </summary>
    /// <param name="item">The book item to resume (chapter or single-file book).</param>
    /// <param name="fallbackTicks">The caller's best position (server progress or session offset; item-absolute for a book, which never transcodes).</param>
    /// <param name="user">The user for the stream URL.</param>
    /// <param name="context">The Alexa context, for the JF-505 screenless-device check.</param>
    /// <param name="request">The skill request, for the JF-501 progressive announce vehicle.</param>
    /// <param name="locale">The request locale for response strings.</param>
    /// <returns>The book resume response, or null when this is not a flag-on book resume.</returns>
    private async Task<SkillResponse?> TryBuildNativeControlsBookResumeAsync(
        BaseItem? item,
        long fallbackTicks,
        Entities.User user,
        Context? context,
        Request? request,
        string locale)
    {
        if (item is not MediaBrowser.Controller.Entities.AudioBook
            || Plugin.Instance?.Configuration?.NativeControlsForBooks != true)
        {
            return null;
        }

        string bookKey = ResumeMath.GetAudiobookBookKey(item);
        long startTicks = ResumeMath.GetAudiobookStartTicks(bookKey, fallbackTicks);

        Logger.LogInformation(
            "ResumeIntent: audiobook '{BookName}' ({BookKey}) routes to the VideoApp HLS playlist, startTicks={StartTicks} (tracker first, fallback={FallbackTicks})",
            item.Name, bookKey, startTicks, fallbackTicks);

        if (startTicks <= 0)
        {
            // No position in either source: the same fresh VideoApp launch PlayBook's
            // no-progress path uses (no start slice), kept silent like the flat tail.
            return BuildVideoAppAudioResponse(item.Id.ToString(), item, user, context: context);
        }

        SkillResponse bookResponse = BuildAudiobookResumeResponse(item, startTicks, user, context);

        // JF-501: the announce rides the progressive vehicle on a VideoApp launch (a
        // directive-only final response can have its speech cut); non-intent requests
        // and failed sends keep it on the final response.
        bookResponse.Response.OutputSpeech = await SpeakVideoLaunchAnnounceAsync(
            context,
            request,
            new PlainTextOutputSpeech(
                ResponseStrings.Get("NowPlayingWithPosition", locale, item.Name, ResumeMath.FormatPosition(startTicks)))).ConfigureAwait(false);
        return bookResponse;
    }
}
