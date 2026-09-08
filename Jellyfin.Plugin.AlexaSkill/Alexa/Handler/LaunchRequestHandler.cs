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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
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
    /// JF-520/JF-521: the position is classified at seed time; stream-relative only when
    /// the item is transcode-routed, this device's ledger holds a launch base, AND the
    /// UserData position tick-equals this device's own last recorded raw offset (the same
    /// stop event wrote both; a different value was advanced by another client, JF-521 F1).
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
        if (jellyfinUser != null)
        {
            UserItemData? userData = _userDataManager.GetUserData(jellyfinUser, item);
            positionTicks = userData?.PlaybackPositionTicks ?? 0;
        }

        // For audiobooks with native controls, prefer the segment-based tracker position
        // (accurate for HLS concat playback) and signal resume-via-playlist to YesIntent.
        bool useResumePlaylist = false;
        if (item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal)
            && Plugin.Instance?.Configuration?.NativeControlsForBooks == true)
        {
            string bookId = (item.ParentId != Guid.Empty ? item.ParentId : item.Id).ToString("N");
            long trackedTicks = Plugin.Instance?.AudiobookPositionTracker?.GetPositionTicks(bookId) ?? 0;
            Logger.LogDebug(
                "LaunchResume: audiobook resume check bookId={BookId}, trackedTicks={Ticks}, userDataTicks={UserData}",
                bookId, trackedTicks, positionTicks);
            if (trackedTicks > 0)
            {
                positionTicks = trackedTicks;
                useResumePlaylist = true;
            }
        }

        int offsetMs = (int)Math.Min(TimeSpan.FromTicks(positionTicks).TotalMilliseconds, int.MaxValue);

        // JF-520/JF-521 provenance classification (the UserData seed's share of the
        // JF-514 residual). For a transcode-routed Movie/Episode the event writers
        // persist the RAW device offset into UserData (stream-relative; see
        // PlaybackStoppedEventHandler), so the offer must flag those positions for the
        // Yes-side rebase. JF-520 classified on "this device's ledger has a base AND the
        // item routes to the transcode"; the JF-520 review (F1) showed that composes a
        // FORWARD skip whenever UserData was later advanced by ANOTHER source (a phone
        // play, a VideoApp session) while the device's base stays stale: base 20min +
        // phone-watched 40min minted ?start=60min on a 40min-true position.
        //
        // JF-521 DESIGN DECISION: fix the READER (this classification), not the writer.
        // The writer-side alternative (event handlers persist item-absolute positions by
        // adding the recorded launch base at stop time) was evaluated and rejected: (1)
        // the writers feed four stores (server PlayState, DeviceQueue.CurrentPositionTicks,
        // ItemPositionState, Jellyfin UserData) read by ~19 sites, and every compensating
        // reader would have to flip in the same change or double-add; most sharply the
        // ResumeIntent tail, whose streamRelative=true is unconditional for three
        // fallbacks of which only two are plugin-written (the AudioPlayer context offset
        // is written by AMAZON and stays stream-relative); (2) the stop-time base read is
        // unsafe: the ledger is a last-RESOLVE ledger, and PlaybackNearlyFinished
        // resolves the wrapped/repeat-one next item (the SAME item, at offset 0) during
        // playback, zeroing its base mid-playback (the JF-520 seed-binding refutation
        // race), so the writer would persist a wrong "item-absolute" value; (3) those
        // errors land in server-PERSISTENT UserData (cross-client visible, survives
        // restarts) and a rolling deploy leaves pre-deploy stream-relative values with
        // no provenance flag. A misclassification HERE costs one transient ?start=.
        //
        // The discriminator that closes F1: TICK-EXACT EQUALITY against THIS device's own
        // last-persisted raw offset (ItemPositionState, same stop event that filled
        // UserData). PlaybackStoppedEventHandler writes the SAME raw ticks into both, so
        // UserData == the device's recorded offset iff the last UserData write was THIS
        // device's audio-shaped stop (stream-relative); any other value was advanced by
        // another source (item-absolute, no rebase). The JF-520 recorded-base test stays
        // as a precondition (a raw-static audio launch records base 0 = no-op rebase, so
        // only a base > 0 can matter), the equality check runs second, and the codec DB
        // probe runs last (the JF-520 ledger-first operand order). Residuals, all bounded
        // and conservative (restart earlier by the base, never the F1 forward skip):
        // ItemPositionState trimmed (cap 200) or cleared while the base survives reads
        // null -> item-absolute; a second Echo's stop also breaks equality; a
        // ms-exact coincidence (another client stopping at exactly the device's raw
        // offset) still composes, and the runtime clamp in ResolveResumedAudioLaunch
        // bounds whatever it mints.
        string? ledgerDeviceId = context.System?.Device?.DeviceID;
        long? recordedBaseMs = GetAudioTranscodeBase(ledgerDeviceId, lastPlayedItemId);
        bool offsetIsStreamRelative = recordedBaseMs is > 0
            && positionTicks > 0
            && GetRecordedDeviceOffsetTicks(ledgerDeviceId, lastPlayedItemId) == positionTicks
            && RoutesToAudioTranscode(item);
        if (offsetIsStreamRelative)
        {
            Logger.LogInformation(
                "LaunchResume: device last-played item {ItemId} routes to the audio-only transcode, this device has a recorded launch base ({BaseMs}ms), and the UserData position matches this device's own last recorded offset; the position is stream-relative, flagging the offer for the confirm-side rebase",
                lastPlayedItemId, recordedBaseMs!.Value);
        }
        else if (recordedBaseMs is > 0)
        {
            Logger.LogInformation(
                "LaunchResume: device last-played item {ItemId} has a recorded launch base ({BaseMs}ms) but the UserData position ({PositionTicks} ticks) does not match this device's own last recorded offset; another source advanced it, so it is item-absolute and must NOT be rebased over the stale base (JF-521 F1)",
                lastPlayedItemId, recordedBaseMs.Value, positionTicks);
        }

        return BuildResumeOfferResponse(item, lastPlayedItemId, offsetMs, user, locale, context, session, useResumePlaylist: useResumePlaylist, offsetIsStreamRelative: offsetIsStreamRelative);
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
    /// <param name="offsetIsStreamRelative">Whether <paramref name="offsetMs"/> counts the previous playback's output timeline (device-derived) and needs the JF-514 rebase on confirm. Set by the AudioPlayer-context seed (always) and by the device-last-played seed when the item is transcode-routed with a recorded base whose UserData position matches this device's own last recorded raw offset (JF-520/JF-521); the audio-only fallback seed never needs it (audio items never route to the transcode).</param>
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
            && IsVideoAppLaunchItem(item)
            && !Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context))
        {
            Logger.LogDebug(
                "LaunchResume: last-played item '{ItemName}' is video but the device has no screen; trying the audio fallback",
                item.Name);
            return BuildScreenlessAudioFallbackOffer(session, user, locale, context);
        }

        string title = item?.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        SkillResponse response = AskLocalized(
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
            AudioLaunchSource source = ResolveAudioLaunchSource(session.FullNowPlayingItem, item_id, user, 0, context?.System?.Device?.DeviceID);
            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source.Url, item_id, session.FullNowPlayingItem, user, context);
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

            AudioLaunchSource source = ResolveAudioLaunchSource(item, item_id, user, 0, context?.System?.Device?.DeviceID);
            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source.Url, item_id, item, user, context);
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
            ? string.Format(CultureInfo.InvariantCulture, ResponseStrings.Get(welcomeSsmlKey, locale), EscapeXml(givenName!))
            : GetSsml("WelcomeSsml", locale);

        string? repromptSsml = GetSsml("WelcomeRepromptSsml", locale);

        SkillResponse response;
        if (welcomeSsml != null && repromptSsml != null)
        {
            response = AskSsml(welcomeSsml, repromptSsml);
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

        string imageUrl = GetImageUrl(itemId, user);
        var directive = Apl.AplHelper.BuildResumeOfferDirective(item, imageUrl, imageUrl, locale, context);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);
        }
    }
}
