using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.PauseIntent, AMAZON.StopIntent, AMAZON.CancelIntent, and
/// the hardware pause button (PlaybackControllerRequestType.Pause).
/// All paths send AudioPlayer.Stop. Stop/cancel end the session; pause ends it
/// unless PauseKeepsSession is on (JF-482 experiment: audio still stops, but the
/// session stays open so bare follow-up commands stay in-skill, and the response
/// speaks a minimal pause word plus a reprompt, JF-488: the silent open session
/// was closed by the platform with EXCEEDED_MAX_REPROMPTS). Alexa routes
/// resume to AMAZON.ResumeIntent automatically when audio was recently stopped.
/// JF-564: during a VideoApp-family medium (video, live TV, a NativeControlsForBooks
/// audiobook) pause and CANCEL cannot be honored as a PAUSE at all (there is no
/// VideoApp.Stop and no pause directive), so instead of the silent no-op stop those
/// cells speak an honest line (session still ends, AudioPlayer.Stop still sent for
/// any displaced audio). Stop keeps the docs-mandated silent shape
/// ("responses to StopIntent must end the session"); an empty ledger (cold device)
/// keeps the music semantics unchanged.
/// JF-847 (2026-10-09 device evidence): the line is STATE-AWARE: a PLAYING
/// context speaks VideoStoppedByVoice (the AudioPlayer.Stop directive reaches the
/// pipeline's active stream), every other activity keeps the honest cannot-pause
/// line. The evidence narrative and the PLAYING-only rule are owned by the branch
/// comment in HandleAsync; the verified platform model lives in the repo-root
/// CLAUDE.md Stop/Session Routing reference.
/// </summary>
public class PauseIntentHandler : BaseHandler
{
    private readonly ILibraryManager? _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PauseIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="libraryManager">The library manager, to resolve the ledger item of the JF-564 medium classification. Null keeps the pre-JF-564 behavior.</param>
    /// <param name="queueManager">Optional per-device queue manager (the last-played ledger the JF-564 medium classification reads).</param>
    public PauseIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        ILibraryManager? libraryManager = null,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && ((string.Equals(intentRequest.Intent.Name, IntentNames.AmazonPause, System.StringComparison.Ordinal) ||
            string.Equals(intentRequest.Intent.Name, IntentNames.AmazonStop, System.StringComparison.Ordinal)) ||
            string.Equals(intentRequest.Intent.Name, IntentNames.AmazonCancel, System.StringComparison.Ordinal))) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Pause);
    }

    /// <summary>
    /// Pause or stop currently playing media.
    /// All paths send AudioPlayer.Stop. During a VideoApp-family medium (JF-564)
    /// pause and cancel speak an honest line with the session ended: the stopped
    /// line when the context's playerActivity is PLAYING (JF-847: the directive
    /// reaches the active stream), otherwise the cannot-pause line.
    /// Otherwise stop/cancel end the session; pause ends it unless PauseKeepsSession
    /// is on (JF-482), in which case the response also speaks a minimal pause word
    /// and carries a reprompt (JF-488; the silent open session timed out on-device).
    /// Pause optionally includes a position card.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        string? intentName = intentRequest?.Intent?.Name;
        bool isStop = string.Equals(intentName, IntentNames.AmazonStop, System.StringComparison.Ordinal);
        bool isCancel = string.Equals(intentName, IntentNames.AmazonCancel, System.StringComparison.Ordinal);

        Logger.LogDebug(
            "PauseIntent: isStop={IsStop}, isCancel={IsCancel}, activity={Activity}, offset={OffsetMs}ms",
            isStop, isCancel, context.AudioPlayer?.PlayerActivity, context.AudioPlayer?.OffsetInMilliseconds);

        // Stop keeps the docs-mandated silent shape: AudioPlayer.Stop plus a session
        // end, no speech ("responses to AMAZON.StopIntent must use shouldEndSession
        // true", the Stop/Session Routing reference).
        if (isStop)
        {
            Logger.LogDebug("PauseIntent: STOP, ending session with AudioPlayer.Stop");
            return Task.FromResult(BuildPauseResponse());
        }

        // JF-564/JF-847: during a VideoApp-family medium pause/cancel cannot PAUSE
        // (no VideoApp.Stop exists), so the skill says so instead of the old silent
        // no-op stop. WHICH line is state-aware per the 2026-10-09 device evidence:
        // the AudioPlayer.Stop directive reliably stops the video iff the request
        // context's AudioPlayer.PlayerActivity is PLAYING (the video is the
        // pipeline's active stream; with a non-PLAYING context the directive was
        // observed inert, the stale-token relaunch shape), so PLAYING speaks the
        // stopped line and everything else the honest cannot-pause line. The check
        // is deliberately PLAYING-only, not IsActivelyPlaying: BUFFER_UNDERRUN is
        // unobserved for a VideoApp stream and the honest refusal is the safe
        // failure when the stop's reach is unproven (ResumeIntentHandler keeps its
        // own narrower check for the same reason). The response keeps the
        // AudioPlayer.Stop directive (the audio-stop invariant; a displaced audio
        // stream must still be told to stop) and ends the session as a Tell: these
        // are IntentRequests, so the JF-299 event-response rules do not apply. An
        // empty ledger (Unknown) falls through to the audio paths below unchanged.
        PlaybackLaunchBuilder.PlayingMedium medium = Launch.ResolvePlayingMedium(context, _libraryManager, _queueManager);
        if (PlaybackLaunchBuilder.IsVideoAppMedium(medium))
        {
            bool pipelineOwnsStream = string.Equals(context.AudioPlayer?.PlayerActivity, "PLAYING", System.StringComparison.Ordinal);
            string lineKey = pipelineOwnsStream ? "VideoStoppedByVoice" : "CannotPauseVideoByVoice";
            Logger.LogDebug(
                "PauseIntent: {Medium} playing, activity {Activity}, speaking {LineKey}",
                medium, context.AudioPlayer?.PlayerActivity, lineKey);
            SkillResponse honest = BuildPauseResponse();
            honest.Response.OutputSpeech = new PlainTextOutputSpeech(
                ResponseStrings.Get(lineKey, GetLocale(request)));
            return Task.FromResult(honest);
        }

        // Cancel on the audio path: silent AudioPlayer.Stop plus session end (JF-299
        // covers it; the JF-482 experiment does not retest it).
        if (isCancel)
        {
            Logger.LogDebug("PauseIntent: CANCEL, ending session with AudioPlayer.Stop");
            return Task.FromResult(BuildPauseResponse());
        }

        // Pause: AudioPlayer.Stop, session handling per PauseKeepsSession (JF-482
        // experiment; default ON since the JF-488 device verification of 2026-09-05,
        // which re-ran the matrix clean WITH the reprompt). The open-response
        // rationale (minimal speech + reprompt shape) is owned by the
        // BuildPauseResponse doc in BaseHandler. Audio stops in both modes; the
        // optional position card below is unaffected by the flag.
        string locale = GetLocale(request);
        var response = BuildPauseResponse(_config.PauseKeepsSession, locale);

        bool seekEnabled = Plugin.Instance?.Configuration?.SeekEnabled == true;
        bool announcePosition = Plugin.Instance?.Configuration?.PauseAnnouncePosition == true;
        bool hasNowPlaying = session?.FullNowPlayingItem != null;

        if (seekEnabled && announcePosition && hasNowPlaying)
        {
            string positionText = ResumeMath.BuildPositionDisplay(session!, locale);
            if (!string.IsNullOrEmpty(positionText))
            {
                response.Response.Card = new StandardCard
                {
                    Title = session!.FullNowPlayingItem!.Name ?? ResponseStrings.Get("NowPlayingCardTitle", locale),
                    Content = positionText
                };
            }
        }

        return Task.FromResult(response);
    }
}
