using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.RepeatIntent intents (JF-562).
/// Amazon delivers the repeat built-in without the invocation name while (or shortly
/// after) this skill plays media; before this handler it fell through to the
/// controller's CouldNotUnderstand tell. Semantics are per medium: a MUSIC track is
/// restarted from position 0 via AudioPlayer.Play (ReplaceAll, offset 0, silent by
/// default and honoring the AnnounceAudioPlays flag through the shared builder);
/// everything the skill cannot honestly restart mid-play (VideoApp-launched movies,
/// episodes and live TV, audiobooks that ride the AudioPlayer path) gets the
/// localized CannotRepeatContent tell instead of pretending. Known limitation:
/// a VideoApp-launched audiobook (NativeControlsForBooks) is recorded nowhere as
/// the device's last play (the last-played recording deliberately skips the
/// audiobook concat URL to preserve chapter accuracy), so a repeat arriving
/// mid-book cannot see the book and restarts the previously played audio track.
/// This is the SECOND life of this handler: the JF-451-era predecessor answered
/// repeat with now-playing info (MediaInfo's job) and was deleted for it; the
/// restart semantics here are the JF-562 redesign.
/// </summary>
public class RepeatIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepeatIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">The library manager, to resolve the current item from its ID.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="queueManager">Optional per-device queue manager (the last-played record that detects a stale AudioPlayer token and the launch-scope store for the restart).</param>
    public RepeatIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonRepeat, StringComparison.Ordinal);
    }

    /// <summary>
    /// Repeat the current item: music restarts from the beginning, anything the skill
    /// cannot restart mid-play (video, live TV, audiobooks) answers honestly.
    /// </summary>
    /// <param name="request">The skill intent request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>An AudioPlayer restart of the current music track, or the honest cannot-repeat tell.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // Resolve the CURRENT item. The AudioPlayer token is the primary signal (it
        // survives the PlaybackStopped cleanup that clears FullNowPlayingItem, the
        // documented resume gotcha), cross-checked against the per-device last-played
        // record only for the displacement shape: a NON-AudioPlayer launch (movie,
        // episode, live TV) never updates the token but IS recorded as the device's
        // last play, so "token differs from the last play AND the recorded item is
        // video" means the video displaced the audio and is what is playing. Any
        // other mismatch is the ordinary queue-advance shape instead
        // (RecordLastPlayed pins the user-initiated play; the Enqueue directives
        // that advance the queue never record), where the newer token wins.
        string? deviceId = context?.System?.Device?.DeviceID;
        string? lastPlayedId = deviceId != null ? _queueManager?.GetLastPlayedItemId(deviceId) : null;
        string? token = context?.AudioPlayer?.Token;

        BaseItem? ResolveItem(string? id) =>
            !string.IsNullOrEmpty(id) && Guid.TryParse(id, out Guid guid)
                ? _libraryManager.GetItemById(guid)
                : null;

        BaseItem? lastPlayedItem = ResolveItem(lastPlayedId);
        bool videoDisplacedAudio =
            lastPlayedItem != null
            && !string.IsNullOrEmpty(token)
            && !string.Equals(token, lastPlayedId, StringComparison.Ordinal)
            && PlaybackLaunchBuilder.IsVideoAppLaunchItem(lastPlayedItem);

        BaseItem? item;
        if (videoDisplacedAudio)
        {
            Logger.LogInformation(
                "RepeatIntent: AudioPlayer token {Token} was displaced by the VideoApp launch of '{ItemName}' ({ItemId}); the video is current",
                token, lastPlayedItem!.Name, lastPlayedId);
            item = lastPlayedItem;
        }
        else
        {
            item = ResolveItem(token) ?? session?.FullNowPlayingItem ?? lastPlayedItem;
        }

        if (item == null)
        {
            Logger.LogDebug(
                "RepeatIntent: no resolvable current item (token={Token}, lastPlayed={LastPlayed}, sessionItem={SessionItem})",
                token, lastPlayedId, session?.FullNowPlayingItem?.Id);
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // MUSIC restarts. AudioBook subclasses Audio (verified against the
        // Jellyfin.Controller source at v10.11.8 and v12.0-rc7), so the exclusion
        // must come first: a book is not a repeatable track.
        if (item is MediaBrowser.Controller.Entities.Audio.Audio
            && item is not MediaBrowser.Controller.Entities.AudioBook)
        {
            string id = item.Id.ToString();
            Logger.LogInformation("RepeatIntent: restarting current track '{ItemName}' ({ItemId}) from the beginning", item.Name, id);

            AudioLaunchSource source = Launch.ResolveAudioLaunchSource(item, id, user, 0);
            return Task.FromResult<SkillResponse>(Launch.BuildAudioPlayerResponse(
                PlayBehavior.ReplaceAll,
                source,
                id,
                item,
                user,
                context,
                announceLocale: locale,
                queueManager: _queueManager));
        }

        Logger.LogDebug(
            "RepeatIntent: current item '{ItemName}' ({ItemType}) is not repeatable media, answering honestly",
            item.Name, item.GetType().Name);
        return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("CannotRepeatContent", locale)));
    }
}
