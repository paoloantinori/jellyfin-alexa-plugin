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
/// localized CannotRepeatContent tell instead of pretending. FIXED by JF-566:
/// a repeat arriving mid-VideoApp-audiobook now resolves the book (the ledger
/// has recorded VideoApp book launches since JF-563, and the JF-568 route
/// marker lets the displacement rule recognize a VideoApp-routed book the same
/// way it recognizes a video) and answers the honest CannotRepeatContent tell
/// instead of restarting the previously played audio track. This is the SECOND life of this handler: the JF-451-era predecessor answered
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

        // JF-626 review: during a VideoApp-family medium the only restart mechanism
        // Repeat has (AudioPlayer.Play ReplaceAll) would be a parallel stream the
        // platform cannot stop (no VideoApp.Stop exists; for the JF-625 seek-mode
        // VideoAppAudio medium the shared resolver now correctly hands back the
        // seek-mode track, and restarting it would double the audio of the very
        // item playing), so the honest answer is the same cannot-repeat tell the
        // non-restartable kinds get below (the PauseIntentHandler JF-564 precedent).
        // Unknown (no ledger record) keeps the audio paths unchanged.
        PlaybackLaunchBuilder.PlayingMedium medium = Launch.ResolvePlayingMedium(context, _libraryManager, _queueManager);
        if (PlaybackLaunchBuilder.IsVideoAppMedium(medium))
        {
            Logger.LogDebug("RepeatIntent: {Medium} playing, cannot honestly restart", medium);
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("CannotRepeatContent", locale)));
        }

        // The ONE current-item resolver (JF-626): the codec-safe AudioPlayer token
        // (a sleep-timer composite token still parses, JF-447; the pre-JF-626 raw
        // Guid.TryParse silently declined to the session/ledger legs while a timer
        // was armed), the session item, and the device-ledger displacement
        // arbitration whose predicate and rationale live in
        // PlaybackLaunchBuilder.ResolveCurrentPlayingItem.
        BaseItem? item = Launch.ResolveCurrentPlayingItem(
            context, session, _libraryManager, _queueManager, "RepeatIntent");

        if (item == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // MUSIC restarts. AudioBook subclasses Audio (verified against the
        // Jellyfin.Controller source at v10.11.8 and v12.0.0), so the exclusion
        // must come first: a book is not a repeatable track.
        if (item is MediaBrowser.Controller.Entities.Audio.Audio
            && !AudiobookItems.IsAudioBook(item))
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
