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
/// JF-847/JF-868 (device evidence 2026-10-09 and 2026-10-11): the line is
/// ALWAYS a stopped-by-voice Tell for a VideoApp-family medium. The 2026-10-09
/// PLAYING observation is one device state among several; the 2026-10-11
/// sessions (corr ab55dc39, de7f892a) are the common VideoApp shape: the context
/// reports a bare STOPPED with no token while the video actively plays, yet the
/// AudioPlayer.Stop directive DOES stop it. The former state-aware branch spoke
/// "cannot pause" while the video stopped, contradicting the behavior; the
/// playerActivity is now logged for triage only. JF-870: the Tell names the
/// medium, VideoStoppedByVoice everywhere except the audiobook branch, which
/// speaks BookStoppedByVoice. The platform model lives in the
/// repo-root CLAUDE.md Stop/Session Routing reference.
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
    /// pause and cancel speak the medium's stopped line with the session ended
    /// (JF-868: always, regardless of the context's playerActivity).
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

        // JF-564/JF-868: during a VideoApp-family medium pause/cancel cannot PAUSE
        // (no VideoApp.Stop exists), so the skill speaks the medium's stopped line
        // instead of the old silent no-op stop. The line is ALWAYS a stopped Tell,
        // superseding the JF-847 PLAYING-only rule: the 2026-10-11 device sessions
        // (corr ab55dc39, de7f892a) showed the common VideoApp shape: the context
        // reports a bare STOPPED (no token, offset 0 or stale) while the video
        // actively plays, and the AudioPlayer.Stop directive DOES stop it, so the
        // former cannot-pause line contradicted the actual behavior. The
        // playerActivity is logged above for triage only. The response keeps the
        // AudioPlayer.Stop directive (the audio-stop invariant; a displaced audio
        // stream must still be told to stop) and ends the session as a Tell: these
        // are IntentRequests, so the JF-299 event-response rules do not apply. An
        // empty ledger (Unknown) falls through to the audio paths below unchanged.
        // JF-858: the corroborated flat-gate answer - aged and evidence-free
        // degrades to Unknown, so this branch never speaks the VideoApp line over an
        // idle device (the AudioPlayer.Stop directive is identical on both paths;
        // only the line differed). The decision record lives on
        // ResolvePlayingMediumForFlatTransportGate.
        PlaybackLaunchBuilder.PlayingMedium medium = Launch.ResolvePlayingMediumForFlatTransportGate(context, session, _libraryManager, _queueManager);
        if (PlaybackLaunchBuilder.IsVideoAppMedium(medium))
        {
            // JF-870: the stopped line names the medium. The audiobook branch
            // speaks the book line (a stopped book is not a stopped video, the
            // 2026-10-12 device round's "fermato il video" over an audiobook);
            // every other VideoApp medium (video, live TV, seek-mode music)
            // keeps the video line.
            string lineKey = medium == PlaybackLaunchBuilder.PlayingMedium.VideoAppAudiobook
                ? "BookStoppedByVoice"
                : "VideoStoppedByVoice";
            Logger.LogDebug(
                "PauseIntent: {Medium} playing, activity {Activity}, speaking {LineKey}",
                medium, context.AudioPlayer?.PlayerActivity, lineKey);
            SkillResponse stopped = BuildPauseResponse();
            stopped.Response.OutputSpeech = new PlainTextOutputSpeech(
                ResponseStrings.Get(lineKey, GetLocale(request)));
            return Task.FromResult(stopped);
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
