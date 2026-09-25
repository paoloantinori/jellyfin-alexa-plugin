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
        if (PlaybackLaunchBuilder.IsVideoAppMedium(medium))
        {
            Logger.LogDebug("SleepTimer: {Medium} playing, refusing the re-issue honestly", medium);
            string refusalKey = medium == PlaybackLaunchBuilder.PlayingMedium.VideoAppAudio
                ? "CannotSetSleepTimerInSeekMode"
                : "CannotSetSleepTimerOverVideo";
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get(refusalKey, locale)));
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

        // JF-522: the sleep re-issue/replay builds its AudioPlayerPlayDirective directly
        // (the one production site outside the BuildAudioPlayerResponse chokepoint), so
        // it must retire the item's launch scope itself: the replay rides the RAW STATIC
        // URL (base 0, the item timeline), and without this write a transcode-launched
        // stream's stale base would compose over the replay's offsets at its events
        // (review JF-522; the double-add is otherwise bounded only by the runtime guard).
        if (itemGuid != Guid.Empty
            && context.GetDeviceId() is { Length: > 0 } deviceId
            && Plugin.Instance?.DeviceQueueManager is { } queues)
        {
            queues.RecordLaunchBase(deviceId, itemGuid.ToString(), 0, enqueued: false);

            // JF-628: the same chokepoint-skipping site owes the chokepoint's OTHER
            // write. RecordLastPlayed's invariant (ResolvePlayingMedium's doc: the
            // ledger is written by every launch site) applies because the re-issue IS
            // a user-initiated ReplaceAll play of this item: without this record the
            // ledger stays pinned on the older launch track while the composite token
            // names the armed track, desyncing every ledger reader (the
            // ResolvePlayingMedium/ResolveCurrentPlayingItem snapshot reads and the
            // JF-619 GetDeviceResumePointer stamp arbitration). The ONE exception is
            // a ledger entry another launch recorded on the VideoApp route for a
            // DIFFERENT item: that shape is a VideoApp launch on screen (which never
            // touches context.AudioPlayer.Token) with the sleep arm resolving the
            // STALE audio token/session, and overwriting the truthful VideoApp record
            // with (stale item, Audio) would poison the medium readers persistently
            // (no event ever re-writes the ledger; review finding on JF-628). The
            // JF-632 medium gate above already refuses the RESOLVABLE shapes of this
            // scenario before any write; this guard stays as the belt for the ones
            // the classifier cannot see (no library manager, unresolvable item).
            (string? ledgerItemId, DeviceQueueManager.LaunchRoute? ledgerRoute) =
                queues.GetLastPlayedSnapshot(deviceId);
            bool ledgerNamesOtherVideoAppItem =
                ledgerRoute == DeviceQueueManager.LaunchRoute.VideoApp
                && Guid.TryParse(ledgerItemId, out Guid ledgerGuid)
                && ledgerGuid != itemGuid;
            if (!ledgerNamesOtherVideoAppItem)
            {
                queues.RecordLastPlayed(
                    deviceId, itemGuid.ToString(), DeviceQueueManager.LaunchRoute.Audio);
            }
        }

        int offsetInMilliseconds = 0;
        if (context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0)
        {
            offsetInMilliseconds = (int)context.AudioPlayer.OffsetInMilliseconds;
        }
        else if (session.PlayState?.PositionTicks != null)
        {
            offsetInMilliseconds = (int)TimeSpan.FromTicks(session.PlayState.PositionTicks.Value).TotalMilliseconds;
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
                        Url = Launch.GetStreamUrl(itemGuid.ToString(), user),
                        Token = itemGuid.ToString(),
                        OffsetInMilliseconds = offsetInMilliseconds
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
                    Url = Launch.GetStreamUrl(itemGuid.ToString(), user),
                    Token = token,
                    OffsetInMilliseconds = offsetInMilliseconds
                }
            }
        };

        // JF-618 platform-truth (live 2026-09-23, log corr f560baa9): the sleep
        // deadline is enforced ONLY at track boundaries (PlaybackNearlyFinished);
        // mid-track there are NO events, and AudioPlayer.Stop exists only inside a
        // response, so a deadline inside the current track stops the music at that
        // track's END. When the runtime is known and the deadline lands mid-track,
        // say so instead of promising a mid-song stop the platform cannot deliver.
        bool stopsAtTrackEnd = false;
        long? runtimeTicks = session.FullNowPlayingItem.RunTimeTicks;
        if (runtimeTicks is > 0)
        {
            long remainingTicks = runtimeTicks.Value - (offsetInMilliseconds * TimeSpan.TicksPerMillisecond);
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
