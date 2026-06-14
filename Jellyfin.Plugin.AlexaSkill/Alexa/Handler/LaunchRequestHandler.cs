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
                return HandleSessionQueueResume(request, user, session);
            }
        }

        // No prior playback — show welcome (with optional APL carousel)
        return await BuildWelcomeResponseAsync(context, user, session, locale, cancellationToken).ConfigureAwait(false);
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

        return BuildResumeOfferResponse(item, itemId, offsetMs, user, locale, context);
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
    /// position from UserData. Used when there is no reliable AudioPlayer token — e.g. the
    /// last play was via VideoApp.Launch (audiobooks, native-controls audio), which never
    /// sets context.AudioPlayer.Token. Returns null if the item no longer exists so the
    /// caller can fall through to another path.
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
        return BuildResumeOfferResponse(item, lastPlayedItemId, offsetMs, user, locale, context, useResumePlaylist);
    }

    /// <summary>
    /// Build the resume-offer response: SSML/plain text prompt, APL screen, and
    /// session attributes storing the resume state for YesIntent confirmation.
    /// Shared by HandleResumeOfferAsync (AudioPlayer context) and ResolveActualLastPlayed (server-side).
    /// </summary>
    private SkillResponse BuildResumeOfferResponse(
        BaseItem? item, string itemId, long offsetMs,
        Entities.User user, string locale, Context context, bool useResumePlaylist = false)
    {
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

        TryAttachResumeOfferScreen(response, item, itemId, user, locale, context);

        var resumeState = new ResumeHelper.ResumeState
        {
            ItemId = itemId,
            OffsetMs = (int)Math.Min(offsetMs, int.MaxValue),
            UseResumePlaylist = useResumePlaylist
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
        if (session.FullNowPlayingItem != null)
        {
            string item_id = session.FullNowPlayingItem.Id.ToString();

            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, session.FullNowPlayingItem, user);
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

            return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, item, user);
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
