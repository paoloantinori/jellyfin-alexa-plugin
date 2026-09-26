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
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SleepTimerIntent. Encodes a stop deadline into the current
/// AudioPlayer token so that <c>PlaybackNearlyFinishedEventHandler</c> can
/// check it and stop playback when the deadline passes. JF-632: over a
/// VideoApp-routed medium no deadline can ever fire and the re-issue would be
/// parallel unstoppable audio, so the handler answers the honest refusal Tell
/// instead (the PauseIntentHandler JF-564 precedent).
/// </summary>
public class SleepTimerIntentHandler : BaseHandler
{
    private readonly ILibraryManager? _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SleepTimerIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="libraryManager">The library manager, to resolve the ledger item of the JF-632 medium classification. Null keeps the pre-JF-632 behavior.</param>
    /// <param name="queueManager">Optional per-device queue manager (the last-played ledger the JF-632 medium classification reads).</param>
    public SleepTimerIntentHandler(
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
        return intentRequest != null
            && string.Equals(intentRequest.Intent.Name, "SleepTimerIntent", StringComparison.Ordinal);
    }

    /// <summary>
    /// Set (or cancel) a sleep timer for the currently playing media, or refuse
    /// honestly when the playing medium is VideoApp-routed (JF-632).
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Skill response with updated AudioPlayer directive.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.SleepTimerEnabled, request) is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        // Extract the sleep_duration slot (JF-618: AMAZON.DURATION; carries its own
        // unit, resolving "cinque minuti"/"trenta secondi"/"mezz'ora" to ISO 8601).
        string? durationSlot = null;
        if (intentRequest.Intent.Slots != null
            && intentRequest.Intent.Slots.TryGetValue("sleep_duration", out Slot? slot))
        {
            durationSlot = slot.Value;
        }

        Logger.LogDebug("SleepTimer: entered, durationSlot={DurationSlot}", durationSlot);

        // JF-550 (dead-mic sweep; JF-549 class).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "SleepTimer") is { } elicitCancel)
        {
            return Task.FromResult(elicitCancel);
        }

        TimeSpan? duration = Util.ResumeMath.ParseAlexaDuration(durationSlot);
        if (duration is null)
        {
            Logger.LogDebug("SleepTimer: invalid duration, eliciting");
            return Task.FromResult(BuildDialogElicitResponse("DidNotCatchSleepTimer", locale, "sleep_duration", IntentNames.SleepTimer, Util.ElicitSlots.For(IntentNames.SleepTimer)));
        }

        // Nothing currently playing.
        if (session.FullNowPlayingItem == null)
        {
            Logger.LogDebug("SleepTimer: no media playing, returning Tell");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // JF-632: the re-issue below is a ReplaceAll AudioPlayer.Play, and a VideoApp
        // launch never touches context.AudioPlayer.Token, so over a VideoApp-routed
        // medium (movie, episode, live TV, a NativeControlsForBooks book, JF-625
        // seek-mode music) this handler would re-issue the STALE audio item over the
        // running video: parallel audio the platform cannot stop (no VideoApp.Stop
        // exists), and no deadline could ever fire anyway (VideoApp playback emits no
        // events; the sleep deadline is enforced only at PlaybackNearlyFinished on the
        // AudioPlayer path). The gate reads the SAME evidence the JF-628 ledger guard
        // below reads (the device ledger, through the ONE classifier), so the refusal
        // and the ledger protection cannot drift; the PauseIntentHandler JF-564
        // transport refusal is the precedent. The seek-mode judgment: that medium IS
        // audio and a timer over it is a legitimate wish, but the re-issue directive
        // would double the audio of the very track playing, and its deadline could
        // not fire on the eventless VideoApp path, so it refuses too (seek-mode line).
        // Unknown (cold ledger, no library manager, unresolvable item) keeps the
        // audio paths unchanged.
        PlaybackLaunchBuilder.PlayingMedium medium = Launch.ResolvePlayingMedium(context, _libraryManager, _queueManager);

        // The review's two proven holes close here: (a) the same-item seek-mode
        // shape - the native-controls delegation re-records the SAME track on the
        // VideoApp route while the token keeps naming it, so the classifier's
        // token-ownership arm answers Audio and the gate above passes (probe-proven:
        // parallel audio of the same track plus the ledger route re-poisoned to
        // Audio); (b) the unresolvable ledger item (deleted movie/book) answers
        // Unknown. ANY VideoApp-routed ledger entry means a VideoApp stream owns the
        // screen, so the re-issue must not ship: this route check needs no item
        // resolution and absorbs the JF-628 belt below entirely.
        string? deviceIdForLedger = context.GetDeviceId() is { Length: > 0 } id ? id : null;
        DeviceQueueManager? queuesForLedger = deviceIdForLedger != null ? _queueManager : null;
        (string? ledgerItemId, DeviceQueueManager.LaunchRoute? ledgerRoute) =
            deviceIdForLedger != null && queuesForLedger != null ? queuesForLedger.GetLastPlayedSnapshot(deviceIdForLedger) : (null, null);
        bool ledgerVideoRouted = ledgerRoute == DeviceQueueManager.LaunchRoute.VideoApp;
        if (PlaybackLaunchBuilder.IsVideoAppMedium(medium) || ledgerVideoRouted)
        {
            Logger.LogDebug("SleepTimer: {Medium} playing (ledgerVideoRouted={LedgerVideoRouted}), refusing the re-issue honestly", medium, ledgerVideoRouted);

            // The line follows the SHAPE of what is on screen: the seek-mode line
            // when music is the VideoApp payload (the classifier's VideoAppAudio, or
            // the same-item shape where the classifier answers Audio because the
            // token still names the seek-launched track), the video-family line
            // otherwise (an unresolvable ledger item defaults there).
            MediaBrowser.Controller.Entities.BaseItem? ledgerKindItem = ledgerVideoRouted && _libraryManager != null && Guid.TryParse(ledgerItemId, out Guid ledgerGuid)
                ? _libraryManager.GetItemById(ledgerGuid)
                : null;
            bool musicShaped = medium == PlaybackLaunchBuilder.PlayingMedium.VideoAppAudio
                || (ledgerVideoRouted && ledgerKindItem is MediaBrowser.Controller.Entities.Audio.Audio);
            string refusalKey = musicShaped
                ? "CannotSetSleepTimerInSeekMode"
                : "CannotSetSleepTimerOverVideo";

            // The JF-564 pause-refusal shape: the honest line rides a response that
            // still carries AudioPlayer.Stop, so any DISPLACED audio under the video
            // (the stale stream this very scenario describes) is cleaned up instead
            // of left running behind the refusal.
            SkillResponse refusal = BaseHandler.BuildPauseResponse(keepSessionOpen: false, locale);
            refusal.Response.OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get(refusalKey, locale));
            return Task.FromResult(refusal);
        }

        string itemId = context.AudioPlayer?.Token ?? session.FullNowPlayingItem.Id.ToString();

        // The item id is CANONICALIZED through the shared StreamTokenCodec before use
        // (JF-447: the format's one owner; the event handlers parse it with the same
        // codec). During sleep playback the current token already carries a sleep
        // suffix, and using the RAW token would put a composite id into the stream URL
        // path (unmatchable) and into the replay Token (the old deadline would persist
        // and the sleep would still fire after a cancel; re-arming used to stack a
        // second suffix whose deadline parse then failed). Both the cancel replay and
        // the re-arm mint below build from the CLEAN id.
        Guid itemGuid = StreamTokenCodec.TryGetItemId(itemId, out Guid parsed)
            ? parsed
            : Guid.TryParse(itemId, out Guid bare) ? bare : Guid.Empty;

        // JF-522/JF-636: the sleep re-issue/replay builds its AudioPlayerPlayDirective
        // directly (the one production site outside the BuildAudioPlayerResponse
        // chokepoint), so it owns its launch scope itself. The replay source is
        // resolved through the ONE resume resolver first (JF-520 doctrine): a
        // device-derived offset counts the OUTPUT timeline of the stream that
        // produced it (an atempo speed stream OR a transcode launch), so it must be
        // rebased against the launch scope's base+rate before it can seek the
        // replay, and a speed-routed item keeps its rate instead of silently
        // reverting to 1x. The scope write below then records the RESOLVED source's
        // base and rate (never a hand-pinned 0 over a speed stream).
        int offsetInMilliseconds = 0;
        bool offsetIsDeviceDerived = context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0;
        if (offsetIsDeviceDerived)
        {
            offsetInMilliseconds = (int)context.AudioPlayer!.OffsetInMilliseconds;
        }
        else if (session.PlayState?.PositionTicks != null)
        {
            offsetInMilliseconds = (int)TimeSpan.FromTicks(session.PlayState.PositionTicks.Value).TotalMilliseconds;
        }

        AudioLaunchSource replaySource = itemGuid == Guid.Empty
            ? new AudioLaunchSource(Launch.GetStreamUrl(Guid.Empty.ToString(), user), offsetInMilliseconds, LaunchBaseMs: 0)
            : Launch.ResolveResumedAudioLaunch(
                session.FullNowPlayingItem,
                itemGuid.ToString(),
                user,
                offsetInMilliseconds,
                offsetIsDeviceDerived,
                context.GetDeviceId() is { Length: > 0 } scopeDeviceId ? scopeDeviceId : null,
                _queueManager,
                "SleepTimer re-issue");

        if (itemGuid != Guid.Empty
            && context.GetDeviceId() is { Length: > 0 } deviceId
            && Plugin.Instance?.DeviceQueueManager is { } queues)
        {
            queues.RecordLaunchBase(deviceId, itemGuid.ToString(), replaySource.LaunchBaseMs, enqueued: false, replaySource.RatePerMille);

            // JF-628: the re-issue IS a user-initiated play, so the ledger names the
            // armed track. The route guard the review proved necessary now lives in
            // the GATE above (it refuses on ANY VideoApp-routed entry, absorbed from
            // this belt); only audio-routed shapes reach this write.
            queues.RecordLastPlayed(deviceId, itemGuid.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        }

        // Cancel mode: a zero duration ("ferma dopo zero", the legacy "0") replays
        // without a sleep deadline. The comparison is on the TimeSpan, NOT a rounded
        // minute count (review finding: PT29S rounded to 0 minutes and CANCELLED
        // instead of arming a 29-second timer).
        if (duration.Value <= TimeSpan.Zero)
        {
            // No-token tell: an unparseable token (and no id left to fall back to)
            // must not mint a replay directive from Guid.Empty, whose stream URL
            // would point at nothing (review finding on the cancel branch).
            if (itemGuid == Guid.Empty)
            {
                Logger.LogDebug("SleepTimer: cancel mode found no parseable item id in token={Token}, returning Tell", itemId);
                return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
            }

            Logger.LogDebug("SleepTimer: cancel mode (duration={Duration}), replaying without deadline", duration.Value);
            var cancelDirective = new AudioPlayerPlayDirective
            {
                PlayBehavior = PlayBehavior.ReplaceAll,
                AudioItem = new AudioItem
                {
                    Stream = new AudioItemStream
                    {
                        // The canonicalized GUID feeds the stream URL (a composite id
                        // in the URL path is unmatchable), and the replay Token is the
                        // CLEAN id string with NO sleep suffix: a cancel replays with
                        // no deadline, so PlaybackNearlyFinished sees nothing to enforce.
                        Url = replaySource.Url,
                        Token = itemGuid.ToString(),
                        OffsetInMilliseconds = replaySource.OffsetMs
                    }
                }
            };

            return Task.FromResult<SkillResponse>(new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    ShouldEndSession = true,
                    OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("CancelSleepTimer", locale)),
                    Directives = new List<IDirective> { cancelDirective }
                }
            });
        }

        // Encode the sleep deadline into the token through the shared StreamTokenCodec
        // (JF-447: the format's one owner; the event handlers parse it with the same
        // codec), from the CLEAN id canonicalized above (minting from a suffixed id
        // would stack a second suffix whose deadline parse then fails).
        long deadlineTicks = (DateTimeOffset.UtcNow + duration.Value).UtcTicks;

        string token = StreamTokenCodec.MintSleepTimerToken(itemGuid, deadlineTicks);

        Logger.LogDebug("SleepTimer: setting {Duration} timer, token={Token}", duration.Value, token);

        var directive = new AudioPlayerPlayDirective
        {
            PlayBehavior = PlayBehavior.ReplaceAll,
            AudioItem = new AudioItem
            {
                Stream = new AudioItemStream
                {
                    // The canonicalized GUID also feeds the stream URL: during sleep
                    // playback the raw token carries the sleep suffix, which would put a
                    // composite id into the URL path (the same re-arm defect family).
                    Url = replaySource.Url,
                    Token = token,
                    OffsetInMilliseconds = replaySource.OffsetMs
                }
            }
        };

        // JF-618 platform-truth (live 2026-09-23, log corr f560baa9): the sleep
        // deadline is enforced ONLY at track boundaries (PlaybackNearlyFinished);
        // mid-track there are NO events, and AudioPlayer.Stop exists only inside a
        // response, so a deadline inside the current track stops the music at that
        // track's END. When the runtime is known and the deadline lands mid-track,
        // say so instead of promising a mid-song stop the platform cannot deliver.
        // JF-636: the remaining-time math needs the CONTENT position; on a
        // transcode/speed stream the resolved source carries it as
        // LaunchBaseMs + OffsetMs (the raw device offset is stream-relative).
        bool stopsAtTrackEnd = false;
        long? runtimeTicks = session.FullNowPlayingItem.RunTimeTicks;
        if (runtimeTicks is > 0)
        {
            long contentPositionMs = replaySource.LaunchBaseMs + replaySource.OffsetMs;
            long remainingTicks = runtimeTicks.Value - (contentPositionMs * TimeSpan.TicksPerMillisecond);
            long remainingMs = remainingTicks / TimeSpan.TicksPerMillisecond;
            stopsAtTrackEnd = duration.Value <= TimeSpan.FromMilliseconds(remainingMs);
        }

        string confirmKey = stopsAtTrackEnd ? "SleepTimerSetTrackEnd" : "SleepTimerSetFor";

        return Task.FromResult<SkillResponse>(new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                OutputSpeech = new PlainTextOutputSpeech(
                    ResponseStrings.Get(confirmKey, locale, Util.ResumeMath.FormatSpokenLargestUnit(duration.Value, locale))),
                Directives = new List<IDirective> { directive }
            }
        });
    }

}
