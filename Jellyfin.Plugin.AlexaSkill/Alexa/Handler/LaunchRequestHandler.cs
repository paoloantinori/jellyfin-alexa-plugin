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
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

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
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly CustomerProfileService _profileService;

    /// <summary>
    /// Initializes a new instance of the <see cref="LaunchRequestHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="userManager">The user manager instance.</param>
    /// <param name="userDataManager">The user data manager instance for progress lookups.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public LaunchRequestHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
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
        ArgumentNullException.ThrowIfNull(session);

        // Check if audio was playing before this re-launch (AudioPlayer context carries the token/offset)
        if (_config.ResumeOfferEnabled)
        {
            if (!string.IsNullOrEmpty(context.AudioPlayer?.Token))
            {
                // When NativeControlsForAudio is enabled, initial playback routes through
                // VideoApp.Launch which does NOT update context.AudioPlayer.Token.
                // The token may be stale — pointing to an item from a previous AudioPlayer session
                // while the actual last-played item was played via VideoApp.
                // Cross-reference with server-side progress to detect and correct this mismatch.
                if (_config.NativeControlsForAudio)
                {
                    var resolved = ResolveActualLastPlayed(context, user, session, locale);
                    if (resolved != null)
                    {
                        return resolved;
                    }
                }

                return await HandleResumeOfferAsync(request, context, user, session!, locale, cancellationToken).ConfigureAwait(false);
            }

            // No AudioPlayer token — but with NativeControlsForAudio the last play may have
            // been via VideoApp.Launch (audiobooks, native-controls audio), which never sets
            // the token. If the device has a recorded last-played item, offer a resume prompt
            // instead of falling through to the legacy auto-play session-queue path.
            if (_config.NativeControlsForAudio)
            {
                string? deviceId = context.System?.Device?.DeviceID;
                string? lastPlayed = !string.IsNullOrEmpty(deviceId)
                    ? Plugin.Instance?.DeviceQueueManager?.GetLastPlayedItemId(deviceId)
                    : null;
                if (!string.IsNullOrEmpty(lastPlayed))
                {
                    Logger.LogDebug("LaunchResume: no AudioPlayer token but device has last-played {ItemId}, offering resume", lastPlayed);
                    SkillResponse? offer = BuildDeviceLastPlayedOffer(context, user, session, locale, lastPlayed);
                    if (offer != null)
                    {
                        return offer;
                    }
                }
            }

            // check if we have any media in the queue (legacy Jellyfin session-based resume)
            if (session.NowPlayingQueue.Count > 0)
            {
                return HandleSessionQueueResume(request, context, user, session);
            }
        }

        // No prior playback — show welcome (with optional APL carousel)
        return await BuildWelcomeResponseAsync(context, user, session, locale, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handle the case where AudioPlayer context indicates prior playback.
    /// Looks up the item, offers a resume confirmation prompt. When the offer builder
    /// declines to offer (JF-505: a video item on a screenless device with no audio
    /// fallback), the welcome response answers instead.
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

        // JF-514: the AudioPlayer context offset is device-derived, i.e. relative to the
        // previous playback's OUTPUT timeline (for a transcode-routed item that timeline
        // starts at the stream's ?start= base). Flag it so the Yes-side resume rebases
        // it against the recorded launch base instead of minting it as item-absolute.
        Logger.LogDebug(
            "LaunchResume: offering resume of {ItemId} from offset {OffsetMs}ms (provenance=AudioPlayer context, stream-relative)",
            itemId, offsetMs);
        SkillResponse? offer = BuildResumeOfferResponse(item, itemId, offsetMs, user, locale, context, session, offsetIsStreamRelative: true);
        return offer ?? await BuildWelcomeResponseAsync(context, user, session, locale, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// When NativeControlsForAudio is enabled, playback routes through VideoApp.Launch
    /// which does not update context.AudioPlayer.Token. The token may be stale.
    /// This method checks the per-device queue's LastPlayedItemId (recorded at the
    /// BuildAudioPlayerResponse chokepoint on every play, including VideoApp and APL
    /// carousel taps) and returns a resume offer for that item if it differs from the
    /// stale AudioPlayer token. Device-specific, so it never surfaces content played on
    /// other clients (e.g. the Jellyfin phone app). Returns null if the token already
    /// matches the device's last-played item (not stale) or if none is recorded.
    /// </summary>
    private SkillResponse? ResolveActualLastPlayed(
        Context context, Entities.User user, SessionInfo session, string locale)
    {
        string? deviceId = context.System?.Device?.DeviceID;
        if (string.IsNullOrEmpty(deviceId))
        {
            Logger.LogDebug("LaunchResume: NativeControlsForAudio stale-token check: no device ID in context, using AudioPlayer token");
            return null;
        }

        string? lastPlayedItemId = Plugin.Instance?.DeviceQueueManager?.GetLastPlayedItemId(deviceId);

        if (string.IsNullOrEmpty(lastPlayedItemId))
        {
            Logger.LogDebug("LaunchResume: NativeControlsForAudio stale-token check: no device last-played recorded, using AudioPlayer token");
            return null;
        }

        string audioPlayerToken = context.AudioPlayer!.Token!;

        if (string.Equals(audioPlayerToken, lastPlayedItemId, StringComparison.Ordinal))
        {
            Logger.LogDebug("LaunchResume: NativeControlsForAudio stale-token check: AudioPlayer token matches device last-played '{ItemId}'", lastPlayedItemId);
            return null;
        }

        // AudioPlayer token is stale — offer resume for the device's actual last-played item.
        Logger.LogInformation(
            "LaunchResume: NativeControlsForAudio stale token detected. AudioPlayer={AudioPlayerToken}, device last-played='{ItemId}'. Using device item.",
            audioPlayerToken, lastPlayedItemId);

        return BuildDeviceLastPlayedOffer(context, user, session, locale, lastPlayedItemId);
    }

    /// <summary>
    /// Build a resume offer for the device's last-played item, looking up its best-known
    /// position from UserData. Used when there is no reliable AudioPlayer token, e.g. the
    /// last play was via VideoApp.Launch for an audiobook or native-controls audio, which
    /// never sets context.AudioPlayer.Token. Returns null if the item no longer exists so the
    /// caller can fall through to another path.
    /// JF-522: the position is item-absolute under the writer contract (the stop event
    /// composes the stream's launch base before persisting), so the offer never flags it
    /// for a rebase; pre-JF-522 leftovers are read conservatively (see the inline
    /// rolling-deploy note).
    /// </summary>
    private SkillResponse? BuildDeviceLastPlayedOffer(
        Context context, Entities.User user, SessionInfo session, string locale, string lastPlayedItemId)
    {
        BaseItem? item = null;
        if (Guid.TryParse(lastPlayedItemId, out Guid itemGuid))
        {
            item = _libraryManager.GetItemById(itemGuid);
        }

        if (item == null)
        {
            Logger.LogDebug("LaunchResume: device last-played item {ItemId} no longer exists", lastPlayedItemId);
            return null;
        }

        var (jellyfinUser, _) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        long positionTicks = 0;
        UserItemData? userData = null;
        if (jellyfinUser != null)
        {
            userData = _userDataManager.GetUserData(jellyfinUser, item);
            positionTicks = userData?.PlaybackPositionTicks ?? 0;
        }

        // JF-581 seed resolution, shared with the JF-565 episode resume slice:
        // UserData first, the plugin-owned ItemPositionState when the server-side
        // write was lost, never over a Played item. The rationale comment lives
        // once, on DeviceQueueManager.ResolveResumeTicks.
        positionTicks = DeviceQueueManager.ResolveResumeTicks(
            Plugin.Instance?.DeviceQueueManager,
            context.System?.Device?.DeviceID,
            lastPlayedItemId,
            positionTicks,
            userData?.Played == true,
            Logger,
            "LaunchResume");

        // For audiobooks with native controls, prefer the segment-based tracker position
        // (accurate for HLS concat playback) and signal resume-via-playlist to YesIntent.
        // Precedence (JF-581 + JF-567): ResolveResumeTicks above is the store fallback for
        // every medium; the tracker read here is the audiobook-specific layer that wins
        // when a capable device played the book via the HLS concat timeline (the store
        // cannot see segment positions). Tracker ticks only override when > 0; otherwise
        // the store-resolved positionTicks stands.
        bool useResumePlaylist = false;
        if (AudiobookItems.IsAudioBook(item)
            && Plugin.Instance?.Configuration?.NativeControlsForBooks == true)
        {
            string bookKey = ResumeMath.GetAudiobookBookKey(item);
            long trackedTicks = ResumeMath.GetAudiobookStartTicks(bookKey, 0);
            Logger.LogDebug(
                "LaunchResume: audiobook resume check bookKey={BookKey}, trackedTicks={Ticks}, userDataTicks={UserData}",
                bookKey, trackedTicks, positionTicks);
            if (trackedTicks > 0)
            {
                positionTicks = trackedTicks;
                useResumePlaylist = true;
            }
        }

        int offsetMs = (int)Math.Min(TimeSpan.FromTicks(positionTicks).TotalMilliseconds, int.MaxValue);

        // JF-522 NEW CONTRACT: the event writers persist ITEM-ABSOLUTE positions into
        // UserData (the stop event composes the stream's launch-scoped base), so this
        // seed classifies the UserData position item-absolute UNCONDITIONALLY: the
        // confirm mints it directly and never adds a base over it.
        //
        // This RETIRES the JF-521 reader-side gate (stream-relative only when a ledger
        // base was recorded AND UserData tick-equalled this device's own last recorded
        // raw offset). The gate cannot survive the writer fix: the same stop event now
        // writes the SAME ITEM-ABSOLUTE ticks into both stores, so the equality still
        // holds while the meaning flipped, and the gate would double-add the base on
        // every post-deploy stop (bounded only by the runtime clamp).
        //
        // ROLLING DEPLOY (the migration story, deliberately conservative): a pre-JF-522
        // deploy leaves stream-relative values in UserData with NO provenance flag, and
        // they are indistinguishable from item-absolute ones (both stores were written
        // raw; the tick-equality proves authorship, not timeline). Reading them as
        // item-absolute mints EARLY by at most the stale launch base - never the F1
        // forward skip - exactly once per affected (user, item): the first post-deploy
        // stop overwrites both stores with item-absolute values and the state heals.
        // The JF-521 runtime clamp in ResolveResumedAudioLaunch stays as the backstop
        // for every stream-relative source that remains (the Amazon context offsets).
        // The persisted queue_*.json files lose their old JF-514 ledger on first load
        // under this deploy (the field was renamed into the launch-scope pair), which
        // is the same conservative fallback: no recorded scope means the writer keeps
        // raw ticks and the reader mints early. ROLLBACK is safe on this seed: the
        // pre-JF-522 DLL's JF-521 gate needs a ledger base to flag stream-relative,
        // and the rolled-back ledger deserializes empty, so it reads these
        // item-absolute positions as item-absolute and mints them directly (the
        // position survives); only its tail's CONTEXT-offset resume drops to a
        // 0-restart (the JF-507 interim rule, conservative).
        if (positionTicks > 0)
        {
            Logger.LogDebug(
                "LaunchResume: device last-played item {ItemId} UserData position {PositionTicks} ticks read as item-absolute (JF-522 writer contract); no rebase on confirm",
                lastPlayedItemId, positionTicks);
        }

        return BuildResumeOfferResponse(item, lastPlayedItemId, offsetMs, user, locale, context, session, useResumePlaylist: useResumePlaylist, offsetIsStreamRelative: false);
    }

    /// <summary>
    /// JF-505: a screenless device (no VideoApp interface) can never honor a resume of a
    /// VIDEO item, so the offer builder declines it and falls back to the most recent
    /// AUDIO item with progress in the per-user last-played ledger (Jellyfin UserData,
    /// DatePlayed-descending). Device evidence 2026-09-06: the ledger offered a
    /// Show-played episode to an Echo Dot, and the accepted offer could only fail there.
    /// Returns null when no audio candidate exists, so the caller skips the offer
    /// entirely (straight to welcome).
    /// </summary>
    /// <param name="session">The Jellyfin session (user resolution).</param>
    /// <param name="user">The plugin user (library access filtering).</param>
    /// <param name="locale">The request locale for response strings.</param>
    /// <param name="context">The Alexa context (screenless detection).</param>
    /// <returns>The audio resume offer, or null when there is nothing offerable.</returns>
    private SkillResponse? BuildScreenlessAudioFallbackOffer(
        SessionInfo session, Entities.User user, string locale, Context context)
    {
        // JF-581: run the plugin-owned stored-position scan BEFORE the user resolve
        // gate: the scan needs only the device queue, so a broken Jellyfin user
        // resolution must not decline an offer the store can still make.
        SkillResponse? storedOffer = TryBuildStoredPositionAudioOffer(session, user, locale, context);
        if (storedOffer != null)
        {
            return storedOffer;
        }

        var (jellyfinUser, _) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (jellyfinUser == null)
        {
            Logger.LogDebug("LaunchResume: screenless audio fallback: could not resolve Jellyfin user, no offer");
            return null;
        }

        BaseItemKind[] audioKinds = FilterByContentAccess(new[] { BaseItemKind.Audio, BaseItemKind.AudioBook });
        var (audioItem, audioTicks) = FindLastPlayedItemWithProgress(
            jellyfinUser, _libraryManager, _userDataManager, user, audioKinds, Logger);
        if (audioItem == null)
        {
            // The stored-position scan already ran above (JF-581); this ledger scan
            // reads Jellyfin UserData, which the incident proved can be flat while
            // the plugin store holds positions. Nothing offerable remains.
            Logger.LogDebug("LaunchResume: screenless device, no audio item with progress to offer");
            return null;
        }

        Logger.LogInformation(
            "LaunchResume: screenless device, offering most recent audio item '{ItemName}' ({ItemId}) instead of the video item",
            audioItem.Name, audioItem.Id);
        // DELIBERATE (JF-505 simplify pin): the offset here is the plain UserData progress,
        // NOT the AudiobookPositionTracker ticks the device-last-played offer prefers above.
        // On a screenless device the audiobook resumes via plain AudioPlayer (useResumePlaylist
        // stays false), and the sliced playlist machinery the tracker serves does not apply:
        // UserData IS the honest position for this shape.
        int audioOffsetMs = (int)Math.Min(TimeSpan.FromTicks(audioTicks).TotalMilliseconds, int.MaxValue);
        return BuildResumeOfferResponse(audioItem, audioItem.Id.ToString(), audioOffsetMs, user, locale, context, session);
    }

    /// <summary>
    /// JF-581: the screenless audio fallback's Jellyfin-UserData ledger scan declines
    /// when the server-side writes never landed. Scan the device queue from the tail
    /// (most recent first) and offer the first queued item holding a recorded
    /// position that the device can actually play (never a VideoApp-launch item, so
    /// the shared builder cannot recurse back into this fallback).
    /// </summary>
    private SkillResponse? TryBuildStoredPositionAudioOffer(SessionInfo session, Entities.User user, string locale, Context context)
    {
        string? deviceId = context.System?.Device?.DeviceID;
        DeviceQueue? queue = string.IsNullOrEmpty(deviceId) ? null : Plugin.Instance?.DeviceQueueManager?.GetQueue(deviceId);
        if (queue == null)
        {
            return null;
        }

        // Candidate order (review finding): queue order is PLAYLIST order, not play
        // recency, so the tail alone can offer an older listen over the most recent
        // one. Try the queue's current-item pointer first (the most recently stopped
        // item), then walk backwards from its index, then the entries above it.
        int startIndex = queue.CurrentIndex >= 0 && queue.CurrentIndex < queue.ItemIds.Count
            ? queue.CurrentIndex
            : queue.ItemIds.Count - 1;
        for (int offset = 0; offset < queue.ItemIds.Count; offset++)
        {
            int i = startIndex - offset;
            if (i < 0)
            {
                i += queue.ItemIds.Count;
            }

            string candidateId = queue.ItemIds[i];
            // ticks != null implies the id parsed (the accessor returns null otherwise)
            long? ticks = Plugin.Instance?.DeviceQueueManager?.GetStoredPositionTicks(deviceId!, candidateId);
            if (ticks == null)
            {
                continue;
            }

            BaseItem? candidate = _libraryManager.GetItemById(Guid.Parse(candidateId));
            if (candidate == null || PlaybackLaunchBuilder.IsVideoAppLaunchItem(candidate))
            {
                continue;
            }

            int offsetMs = (int)Math.Min(TimeSpan.FromTicks(ticks.Value).TotalMilliseconds, int.MaxValue);
            Logger.LogInformation(
                "LaunchResume: screenless audio fallback seeded from ItemPositionState: item={ItemId}, ticks={Ticks}",
                candidateId, ticks.Value);
            return BuildResumeOfferResponse(candidate, candidateId, offsetMs, user, locale, context, session);
        }

        return null;
    }

    /// <summary>
    /// Build the resume-offer response: SSML/plain text prompt, APL screen, and
    /// session attributes storing the resume state for YesIntent confirmation.
    /// Shared by HandleResumeOfferAsync (AudioPlayer context) and ResolveActualLastPlayed (server-side).
    /// JF-505: on a screenless device a VIDEO item (Movie/Episode) is never offered;
    /// the audio fallback replaces it (or the offer is skipped when no audio exists).
    /// </summary>
    /// <param name="item">The item to offer.</param>
    /// <param name="itemId">The item ID stored in the resume state.</param>
    /// <param name="offsetMs">The resume offset.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="context">The Alexa context (screenless detection).</param>
    /// <param name="session">The Jellyfin session (audio fallback lookup).</param>
    /// <param name="useResumePlaylist">Whether YesIntent should resume via the audiobook playlist.</param>
    /// <param name="offsetIsStreamRelative">Whether <paramref name="offsetMs"/> counts the previous playback's output timeline (device-derived) and needs the JF-514 rebase on confirm. Set ONLY by the AudioPlayer-context seed (Amazon wrote the offset; it is stream-relative by platform contract). The device-last-played and audio-only fallback seeds never set it since JF-522 (their positions are item-absolute under the writer contract).</param>
    /// <returns>The offer, or null when the device cannot play the offered item and no audio fallback exists.</returns>
    private SkillResponse? BuildResumeOfferResponse(
        BaseItem? item, string itemId, long offsetMs,
        Entities.User user, string locale, Context context, SessionInfo session, bool useResumePlaylist = false, bool offsetIsStreamRelative = false)
    {
        // JF-505: never offer a video resume to a device that cannot play it. LiveTvChannel
        // rides the same VideoApp launch path (its static /Audio/ URL 500s on a live source,
        // so confirming the offer on a screenless device can never succeed). The shared
        // predicate owns the kind list.
        if (item != null
            && PlaybackLaunchBuilder.IsVideoAppLaunchItem(item)
            && !Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
        {
            Logger.LogDebug(
                "LaunchResume: last-played item '{ItemName}' is video but the device has no screen; trying the audio fallback",
                item.Name);
            return BuildScreenlessAudioFallbackOffer(session, user, locale, context);
        }

        string title = item?.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        SkillResponse response = SpeechBuilder.AskLocalized(
            "ResumePromptSsml", "ResumePrompt", "ResumeReprompt", locale, title);

        TryAttachResumeOfferScreen(response, item, itemId, user, locale, context);

        var resumeState = new ResumeHelper.ResumeState
        {
            ItemId = itemId,
            OffsetMs = (int)Math.Min(offsetMs, int.MaxValue),
            UseResumePlaylist = useResumePlaylist,
            OffsetIsStreamRelative = offsetIsStreamRelative
        };

        response.SessionAttributes = new Dictionary<string, object>
        {
            ["resume_state"] = JsonConvert.SerializeObject(resumeState)
        };

        // JF-398: activating the resume-offer flow supersedes any other flow's state.
        ConversationalFlows.MarkOthersInactive(response, ConversationalFlows.ResumeKeys);
        return response;
    }

    /// <summary>
    /// Handle the legacy Jellyfin session-based queue resume (no confirmation prompt).
    /// </summary>
    private SkillResponse HandleSessionQueueResume(Request request, Context context, Entities.User user, SessionInfo session)
    {
        if (session.FullNowPlayingItem != null)
        {
            string item_id = session.FullNowPlayingItem.Id.ToString();

            // JF-507: shared codec-gated audio-launch decision (an EAC3-family video
            // item on the audio path routes to the audio-only episode HLS transcode).
            AudioLaunchSource source = Launch.ResolveAudioLaunchSource(session.FullNowPlayingItem, item_id, user, 0);
            return Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source, item_id, session.FullNowPlayingItem, user, context);
        }
        else
        {
            BaseItem? item = _libraryManager.GetItemById(session.NowPlayingQueue[0].Id);
            if (item == null)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", GetLocale(request)));
            }

            string item_id = item.Id.ToString();
            session.FullNowPlayingItem = item;

            AudioLaunchSource source = Launch.ResolveAudioLaunchSource(item, item_id, user, 0);
            return Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source, item_id, item, user, context);
        }
    }

    /// <summary>
    /// Build the welcome response with optional personalization and APL carousel
    /// showing recently played items when the device supports APL.
    /// </summary>
    private async Task<SkillResponse> BuildWelcomeResponseAsync(Context context, Entities.User user, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        string? givenName = await _profileService.GetGivenNameAsync(context, cancellationToken).ConfigureAwait(false);

        string welcomeText = !string.IsNullOrEmpty(givenName)
            ? ResponseStrings.Get("WelcomePersonalized", locale, givenName!)
            : ResponseStrings.Get("Welcome", locale);

        string welcomeSsmlKey = !string.IsNullOrEmpty(givenName) ? "WelcomePersonalizedSsml" : "WelcomeSsml";
        string? welcomeSsml = !string.IsNullOrEmpty(givenName)
            ? string.Format(CultureInfo.InvariantCulture, ResponseStrings.Get(welcomeSsmlKey, locale), SpeechBuilder.EscapeXml(givenName!))
            : SpeechBuilder.GetSsml("WelcomeSsml", locale);

        string? repromptSsml = SpeechBuilder.GetSsml("WelcomeRepromptSsml", locale);

        SkillResponse response;
        if (welcomeSsml != null && repromptSsml != null)
        {
            response = SpeechBuilder.AskSsml(welcomeSsml, repromptSsml);
        }
        else
        {
            response = ResponseBuilder.Ask(
                welcomeText,
                new Reprompt(ResponseStrings.Get("WelcomeReprompt", locale)));
        }

        // Attach welcome APL splash screen (always when supported, with or without recently played items)
        TryAttachWelcomeScreen(response, context, user, session, locale, givenName);

        return response;
    }

    /// <summary>
    /// Query recently played items from Jellyfin and return them as display items
    /// suitable for an APL carousel. Deduplicates by name (keeps first = most recent),
    /// applies per-user library filtering, and respects feature flags for media types.
    /// Moved here from BaseHandler (JF-315 batch 11): this welcome screen is the
    /// member's ONE caller, so it lives beside its consumer instead of the
    /// 61-handler base. Internal (was private protected on the base) for the
    /// InternalsVisibleTo test seam (the batch-9 BuildVideoLaunchSpeech precedent);
    /// GetRecentlyPlayedItemsTests targets it directly.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library access and image URL generation.</param>
    /// <param name="libraryManager">The library manager for querying items.</param>
    /// <param name="config">Plugin configuration for feature flags and server address.</param>
    /// <returns>A list of display items (empty, never null).</returns>
    internal static List<Apl.ListDisplayItem> GetRecentlyPlayedItems(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        PluginConfiguration config)
    {
        var itemTypes = new List<BaseItemKind>();
        if (config.MusicEnabled)
        {
            itemTypes.Add(BaseItemKind.Audio);
        }

        if (config.VideosEnabled)
        {
            itemTypes.Add(BaseItemKind.Movie);
            itemTypes.Add(BaseItemKind.Episode);
        }

        if (config.BooksEnabled)
        {
            itemTypes.Add(BaseItemKind.AudioBook);
        }

        if (itemTypes.Count == 0)
        {
            return new List<Apl.ListDisplayItem>();
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = itemTypes.ToArray(),
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 20,
            DtoOptions = new DtoOptions(true)
        };

        ApplyLibraryFilter(query, user, libraryManager);

        IReadOnlyList<BaseItem> recentItems = libraryManager.GetItemList(query) ?? Array.Empty<BaseItem>();

        var results = new List<Apl.ListDisplayItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BaseItem item in recentItems)
        {
            if (results.Count >= 10)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            // Deduplicate by name to avoid "Song X" appearing twice
            if (!seenNames.Add(item.Name))
            {
                continue;
            }

            string subtitle = Apl.AplHelper.GetSubtitle(item);
            string artUrl = new Uri(new Uri(config.ServerAddress), "Items/" + item.Id + "/Images/Primary?api_key=" + user.JellyfinToken).ToString();

            results.Add(new Apl.ListDisplayItem(
                item.Name,
                item.Id.ToString(),
                subtitle,
                artUrl));
        }

        return results;
    }

    /// <summary>
    /// Attach an APL welcome/splash screen directive showing Jellyfin branding,
    /// a personalized greeting, and optionally a "Recently Played" carousel.
    /// Always renders on APL-capable devices (even without recently played items),
    /// giving a consistent branded experience when the skill opens.
    /// </summary>
    private void TryAttachWelcomeScreen(SkillResponse response, Context context, Entities.User user, SessionInfo session, string locale, string? givenName)
    {
        if (!Apl.AplHelper.DeviceSupportsApl(context))
        {
            return;
        }

        if (!Apl.AplHelper.VisualsEnabled)
        {
            return;
        }

        string visualGreeting = !string.IsNullOrEmpty(givenName)
            ? ResponseStrings.Get("WelcomeAplGreeting", locale, givenName!)
            : string.Empty;

        string prompt = ResponseStrings.Get("CarouselReprompt", locale);
        string recentlyPlayedLabel = ResponseStrings.Get("RecentlyPlayed", locale);

        var items = new List<Apl.ListDisplayItem>();
        var (jellyfinUser, _) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (jellyfinUser != null)
        {
            items = GetRecentlyPlayedItems(jellyfinUser, user, _libraryManager, _config);
        }

        var directive = Apl.AplHelper.BuildWelcomeDirective(visualGreeting, prompt, recentlyPlayedLabel, items, context);
        response.Response.Directives.Add(directive);
    }

    /// <summary>
    /// Attach an APL resume-offer screen showing the content artwork, title,
    /// and a "resume?" prompt on APL-capable devices.
    /// </summary>
    private void TryAttachResumeOfferScreen(SkillResponse response, BaseItem? item, string itemId, Entities.User user, string locale, Context context)
    {
        if (item == null)
        {
            return;
        }

        if (!Apl.AplHelper.DeviceSupportsApl(context))
        {
            return;
        }

        if (!Apl.AplHelper.VisualsEnabled)
        {
            return;
        }

        string imageUrl = Launch.GetImageUrl(itemId, user);
        var directive = Apl.AplHelper.BuildResumeOfferDirective(item, imageUrl, imageUrl, locale, context);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);
        }
    }
}
