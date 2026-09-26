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

        // JF-632 gate (the SleepTimer precedent): a VideoApp-routed medium cannot
        // take the re-issue (parallel unstoppable audio; no VideoApp.Stop exists)
        // and the VideoApp seek path tolerates no launch interaction mid-stream,
        // so the honest refusal answers instead. The route check needs no item
        // resolution and closes the same-item seek-mode hole the classifier's
        // token-ownership arm leaves open.
        string? deviceIdForLedger = context.GetDeviceId() is { Length: > 0 } id ? id : null;
        DeviceQueueManager? queuesForLedger = deviceIdForLedger != null ? _queueManager : null;
        (string? ledgerItemId, DeviceQueueManager.LaunchRoute? ledgerRoute) =
            deviceIdForLedger != null && queuesForLedger != null ? queuesForLedger.GetLastPlayedSnapshot(deviceIdForLedger) : (null, null);
        bool ledgerVideoRouted = ledgerRoute == DeviceQueueManager.LaunchRoute.VideoApp;
        PlaybackLaunchBuilder.PlayingMedium medium = Launch.ResolvePlayingMedium(context, _libraryManager, _queueManager);
        if (PlaybackLaunchBuilder.IsVideoAppMedium(medium) || ledgerVideoRouted)
        {
            Logger.LogDebug("SetPlaybackSpeed: {Medium} playing (ledgerVideoRouted={LedgerVideoRouted}), refusing the re-launch honestly", medium, ledgerVideoRouted);

            // The JF-564/JF-632 refusal shape: the honest line rides a response
            // that still carries AudioPlayer.Stop, so any DISPLACED audio under
            // the video is cleaned up instead of left running behind the refusal.
            SkillResponse refusal = BaseHandler.BuildPauseResponse(keepSessionOpen: false, locale);
            refusal.Response.OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("CannotChangeSpeedOverVideo", locale));
            return Task.FromResult<SkillResponse>(refusal);
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

        // The standing preference (JF-636): every successful speed ask persists
        // the resulting rate, so future podcast plays start at it (read by
        // PodcastEpisodeResolver through ResolveStandingRate). Cross-medium by
        // design: the ONE rate preference is whatever the user last asked for
        // explicitly (a "faster" over a song sets the podcast rate too), because
        // no reliable discriminator exists between album-shape podcast episodes
        // and music tracks, and guessing wrong on MUSIC (2x songs) is the worse
        // failure. Clear it with "normal speed" or the config API.
        user.PodcastSpeedPerMille = targetRate;
        Plugin.Instance?.SaveConfiguration();

        Logger.LogInformation(
            "SetPlaybackSpeed: re-launching '{ItemName}' ({ItemId}) at rate {TargetRate}/1000 (from {CurrentRate}/1000), content position {ContentTicks} ticks (raw offset {RawOffsetMs}ms)",
            item.Name, item.Id, targetRate, currentRate, startTicks, rawOffsetMs);

        AudioLaunchSource source = Launch.ResolveAudioLaunchSource(item, itemId, user, offsetMs, ratePerMille: targetRate);
        SkillResponse response = Launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            source,
            itemId,
            item,
            user,
            context);
        response.Response.OutputSpeech = new PlainTextOutputSpeech(
            ResponseStrings.Get("PlaybackSpeedSet", locale, ResponseStrings.Get($"SpeedName{targetRate}", locale)));
        return Task.FromResult<SkillResponse>(response);
    }
}
