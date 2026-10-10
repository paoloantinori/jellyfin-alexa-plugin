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
/// Handler for SetPlaybackSpeedIntent (JF-636): changes the playback speed of
/// the currently playing item in 0.25x steps (0.75x..2.0x) by RE-LAUNCHING the
/// same item at the server-side atempo endpoint from the rate-adjusted current
/// position. Custom skills have no native rate control (the MSAPI-only platform
/// limit as the scrubber), so the re-launch IS the mechanism; it only works on
/// the AudioPlayer path, where a ReplaceAll re-issue is normal queue behavior.
/// Over a VideoApp-routed medium the handler answers the honest refusal (the
/// JF-632 SleepTimer precedent: a re-issue would be parallel unstoppable audio,
/// and the VideoApp seek path tolerates no launch interaction mid-stream).
/// JF-655: the Audio verdict alone does not license the re-launch either: the
/// medium classifier reads the PERSISTENT last-played ledger, so the re-launch
/// additionally requires the active-playback signal (the event-owned flag set by
/// PlaybackStarted, or the request's own PLAYING report); a stale ledger alone
/// answers the no-media Tell.
/// Position math (the load-bearing part): the device reports a STREAM-relative
/// offset on the rate-adjusted output timeline, so the content position is
/// composed through the shared event-side chokepoint
/// (<see cref="ProgressReporter.ComposeEventPositionTicks"/>, which scales by
/// the launch scope's rate and adds its base), and the re-launch converts back
/// by seeking the content position in the new stream's URL (the endpoint's
/// input seek); the stored/derived positions stay CONTENT-relative everywhere
/// else, so existing arbitration is untouched.
/// </summary>
public class SetPlaybackSpeedIntentHandler : BaseHandler
{
    private readonly ILibraryManager? _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetPlaybackSpeedIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="libraryManager">The library manager, to resolve the current item and the medium classification. Null keeps the no-op degrade shapes.</param>
    /// <param name="queueManager">The device queue manager (the last-played ledger and the launch-scope rate/base the position math reads).</param>
    public SetPlaybackSpeedIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.SetPlaybackSpeed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Change the playback speed of the current item (re-launch at the atempo
    /// endpoint from the rate-adjusted position), or refuse honestly over a
    /// VideoApp-routed medium.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Skill response with the speed re-launch AudioPlayer directive, the refusal Tell, or an elicit.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        // JF-550 (dead-mic sweep; JF-549 class): a captured cancel word ends the flow.
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "SetPlaybackSpeed") is { } elicitCancel)
        {
            return Task.FromResult<SkillResponse>(elicitCancel);
        }

        Slot? speedSlot = intentRequest.Intent.Slots != null
            && intentRequest.Intent.Slots.TryGetValue(IntentNames.Slots.Speed, out Slot? foundSlot)
            ? foundSlot
            : null;
        PlaybackSpeed.Request speedRequest = PlaybackSpeed.Resolve(speedSlot, locale);

        Logger.LogDebug("SetPlaybackSpeed: entered, locale={Locale}, slotValue={SlotValue}, kind={Kind}", locale, speedSlot?.Value, speedRequest.Kind);

        if (speedRequest.Kind == PlaybackSpeed.RequestKind.None)
        {
            // JF-549 shape: ask with the mic open, never a dead-mic Tell (the
            // intent is registered in dialog.intents for the elicit).
            return Task.FromResult<SkillResponse>(BuildDialogElicitResponse(
                "DidNotCatchSpeed", locale, IntentNames.Slots.Speed,
                IntentNames.SetPlaybackSpeed, Util.ElicitSlots.For(IntentNames.SetPlaybackSpeed)));
        }

        BaseItem? item = _libraryManager == null
            ? null
            : Launch.ResolveCurrentPlayingItem(context, session, _libraryManager, _queueManager, "SetPlaybackSpeed");
        if (item == null)
        {
            Logger.LogDebug("SetPlaybackSpeed: no resolvable current item, returning Tell");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // JF-632 gate (the SleepTimer precedent), on the ONE shared screen-owner
        // resolver since JF-637 (this block previously inlined the classifier +
        // raw ledger-route belt the resolver now owns): a VideoApp-routed medium
        // cannot take the re-issue (parallel unstoppable audio; no VideoApp.Stop
        // exists) and the VideoApp seek path tolerates no launch interaction
        // mid-stream, so the honest refusal answers instead. The resolver's belt
        // closes the same-item seek-mode hole the classifier's token-ownership
        // arm leaves open.
        string? deviceIdForLedger = context.GetDeviceId() is { Length: > 0 } id ? id : null;
        PlaybackLaunchBuilder.PlayingMedium medium = Launch.ResolveScreenOwningMedium(context, _libraryManager, _queueManager);
        if (PlaybackLaunchBuilder.IsVideoAppMedium(medium))
        {
            Logger.LogDebug("SetPlaybackSpeed: {Medium} playing, refusing the re-launch honestly", medium);

            // The JF-564/JF-632 refusal shape: the honest line rides a response
            // that still carries AudioPlayer.Stop, so any DISPLACED audio under
            // the video is cleaned up instead of left running behind the refusal.
            SkillResponse refusal = BaseHandler.BuildPauseResponse(keepSessionOpen: false, locale);
            refusal.Response.OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("CannotChangeSpeedOverVideo", locale));
            return Task.FromResult<SkillResponse>(refusal);
        }

        // JF-655: the Audio verdict above comes from the PERSISTENT last-played
        // ledger, which reads Audio long after playback stops, and this handler had
        // no other guard (the current-item resolve's ledger tail is bounded by the
        // JF-789 recency window, but a WITHIN-window idle device still answers it),
        // so a speed ask on an idle device re-launched the stale track at the new
        // rate (live e2e finding 2026-09-27: 'a velocità uno e mezzo' with nothing
        // playing restarted hours-old audio; days-old shapes are refused earlier by
        // the bounded tail's no-media Tell). The re-launch also
        // requires the active-playback signal (the event-owned flag set by
        // PlaybackStarted, or this request's own PLAYING report); a stale ledger
        // alone answers the honest no-media Tell, the same refusal the cold
        // item-resolve path above owns.
        if (medium == PlaybackLaunchBuilder.PlayingMedium.Audio
            && !PlaybackLaunchBuilder.IsAudioPlaybackActive(context, _queueManager))
        {
            Logger.LogDebug("SetPlaybackSpeed: medium Audio comes from the device ledger with no active playback signal (flag clear, context not playing); returning the no-media Tell");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // Audiobooks refuse too (JF-636 review): a multi-chapter book on the
        // flat-AudioPlayer path plays the CONCAT HLS stream keyed by the book
        // parent, whose timeline spans chapters. The item-keyed atempo re-launch
        // has no chapter/concat handling (a chapter's own bytes are one slice of
        // the book timeline), so it could only mint a wrong seek or a dead
        // encode; the honest refusal names the limit instead. A PLAIN Tell (no
        // Stop directive): the book is legitimately playing and keeps playing.
        if (Util.AudiobookItems.IsAudioBook(item))
        {
            Logger.LogDebug("SetPlaybackSpeed: item '{ItemName}' is an audiobook; the atempo re-launch cannot span the concat timeline, refusing", item.Name);
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(
                ResponseStrings.Get("CannotChangeSpeedForBook", locale)));
        }

        // JF-636 position carry: the device offset counts the CURRENT stream's
        // output timeline, so it composes through the shared event-side chokepoint
        // (launch-scope rate scaling + base, JF-522) ONLY when the token names the
        // resolved item; otherwise the session's server-side PlayState position
        // (already content-relative) is the truth. Both yield a CONTENT position.
        string itemId = item.Id.ToString();
        long contentTicks = 0;
        long rawOffsetMs = context.AudioPlayer?.OffsetInMilliseconds ?? 0;
        bool tokenNamesItem = StreamTokenCodec.TryGetItemId(context.AudioPlayer?.Token, out Guid tokenItemId)
            && tokenItemId == item.Id;
        if (tokenNamesItem && rawOffsetMs > 0)
        {
            contentTicks = Progress.ComposeEventPositionTicks(
                deviceIdForLedger, item.Id, rawOffsetMs, "SetPlaybackSpeed", _queueManager, _libraryManager);
        }
        else if (session.PlayState?.PositionTicks is > 0)
        {
            contentTicks = session.PlayState.PositionTicks.Value;
        }

        // The new rate: a direct ask names it; a cycle step moves one step from
        // the current rate (the ACTIVE launch-scope rate during playback, the
        // standing preference as the seed when no scope is recorded).
        int currentRate = PlaybackSpeed.NormalPerMille;
        if (tokenNamesItem && deviceIdForLedger != null)
        {
            currentRate = Launch.GetActivePlaybackRate(deviceIdForLedger, itemId, _queueManager) ?? PlaybackSpeed.ResolveStandingRate(user);
        }
        else
        {
            currentRate = PlaybackSpeed.ResolveStandingRate(user);
        }

        int targetRate = speedRequest.Kind switch
        {
            PlaybackSpeed.RequestKind.DirectRate => speedRequest.PerMille,
            PlaybackSpeed.RequestKind.Faster => PlaybackSpeed.Step(currentRate, +1),
            _ => PlaybackSpeed.Step(currentRate, -1),
        };

        // Fail-closed clamp (the JF-565 shape): a position at or beyond the runtime
        // cannot be a legitimate mid-item position, and the re-launch plays from 0.
        long startTicks = Launch.ClampResumeTicksToRuntime(item, contentTicks, "SetPlaybackSpeed");
        int offsetMs = Util.ResumeMath.TicksToMs(startTicks);

        // The standing preference (JF-636): every DELIVERED speed ask persists
        // the resulting rate, so future podcast plays start at it (read by
        // PodcastEpisodeResolver through ResolveStandingRate). Cross-medium by
        // design: the ONE rate preference is whatever the user last asked for
        // explicitly (a "faster" over a song sets the podcast rate too), because
        // no reliable discriminator exists between album-shape podcast episodes
        // and music tracks, and guessing wrong on MUSIC (2x songs) is the worse
        // failure. Clear it with "normal speed" or the config API.
        // JF-693: the persist and the success speech ride only a DELIVERED launch,
        // so they sit after the JF-687 refusal verdict below (the atempo URL is
        // always token-gated, so an empty StreamTokenSecret refuses the re-launch;
        // a refused ask must neither mutate the persisted preference nor speak a
        // speed change that will not happen).
        AudioLaunchSource source = Launch.ResolveAudioLaunchSource(item, itemId, user, offsetMs, ratePerMille: targetRate);
        SkillResponse response = Launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            source,
            itemId,
            item,
            user,
            context,
            queueManager: _queueManager);

        // JF-699 item 1: the builder either threw the StreamTokenNotConfigured
        // refusal (RequestPipeline answers it; nothing below runs) or delivered the
        // re-launch, so the JF-693 verdict wrapper is gone and the persist + success
        // speech simply follow the delivered launch.
        user.PodcastSpeedPerMille = targetRate;
        Plugin.Instance?.Configuration.PersistUnderLedgerLock();

        Logger.LogInformation(
            "SetPlaybackSpeed: re-launching '{ItemName}' ({ItemId}) at rate {TargetRate}/1000 (from {CurrentRate}/1000), content position {ContentTicks} ticks (raw offset {RawOffsetMs}ms)",
            item.Name, item.Id, targetRate, currentRate, startTicks, rawOffsetMs);

        PlaybackLaunchBuilder.AttachAnnounceIfLaunched(
            response,
            new PlainTextOutputSpeech(
                ResponseStrings.Get("PlaybackSpeedSet", locale, ResponseStrings.Get($"SpeedName{targetRate}", locale))));
        return Task.FromResult<SkillResponse>(response);
    }
}
