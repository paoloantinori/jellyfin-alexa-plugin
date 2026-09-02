using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaybackStarted events.
/// </summary>
#pragma warning disable CA1711
public class PlaybackStartedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly ILibraryManager? _libraryManager;

    /// <summary>
    /// Server playback-start reports slower than this are logged at WARNING (not DEBUG) so
    /// they surface at the default log level without grep-pod (JF-410: live stalls were
    /// 11-20s and only visible by diffing log timestamps).
    /// </summary>
    private const long SlowReportMs = 2000;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="libraryManager">Optional library manager for PreEnqueueOnStart item lookups.</param>
    public PlaybackStartedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        ILibraryManager? libraryManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackStarted;
    }

    /// <summary>
    /// Schedules the server-side playback-start report (position/PlayState update,
    /// off the response path, JF-410) and (when PreEnqueueOnStart is on) pre-computes
    /// the next track's resolution so PlaybackNearlyFinished can respond instantly
    /// when it fires (JF-390). NOTE: Amazon REJECTS AudioPlayer.Play directives in
    /// PlaybackStarted responses ("must not contain more than 0 AudioPlayer.Play
    /// directive(s) for this request type"), so we can only PRE-COMPUTE here, not
    /// pre-enqueue. The pre-computed data is stored in
    /// <see cref="NextTrackPrecomputeCache"/> and consumed by
    /// PlaybackNearlyFinishedEventHandler.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;
        string deviceId = context.System?.Device?.DeviceID ?? string.Empty;

        Logger.LogDebug(
            "PlaybackStarted: item={Token}, offset={OffsetMs}ms, sessionId={SessionId}",
            req.Token, req.OffsetInMilliseconds, session.Id);

        long startTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks;
        PlaybackStartInfo playbackStartInfo = new PlaybackStartInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(req.Token),
            PlaybackOrder = session.PlayState.PlaybackOrder,
            RepeatMode = session.PlayState.RepeatMode,
            PositionTicks = startTicks,
            PlaybackStartTimeTicks = startTicks,
        };

        // JF-410: report playback to the server OUTSIDE the Alexa response path. This call
        // stalled 11.3s/20.6s inside Jellyfin on-device (twice on 2026-08-28, breaching
        // Alexa's ~8s window and surfacing as INVALID_RESPONSE "Qualcosa è andato storto"),
        // and nothing in the keep-alive ack depends on its result. Elapsed time is logged
        // (warning above SlowReportMs) so future stalls localize to this call immediately.
        // JF-425: fire-and-forget removed start-vs-stop ordering, so the generation is
        // opened BEFORE dispatch and the report corrects itself if a stop supersedes it
        // mid-flight (see PlaybackReportOrdering).
        PlaybackReportOrdering.BeginStart(deviceId);
        RunFireAndForget(ReportPlaybackStartAsync(playbackStartInfo, deviceId), "PlaybackStartReport");

        // JF-393 diagnostic interaction logging: record playback start so later control
        // intents can report elapsed time since start (JF-392 data collection).
        if (InteractionDiagnostics.IsEnabled(user, _config))
        {
            InteractionDiagnostics.RecordPlaybackStarted(deviceId);
            Logger.LogInformation(
                "[diag] playback started: device={DeviceId} item={Token} sincePlayRequest={SinceRequest}s playIntent={PlayIntent} playSessionNew={PlaySessionNew}",
                deviceId,
                req.Token,
                InteractionDiagnostics.SincePlayRequest(deviceId)?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a",
                InteractionDiagnostics.LastPlayIntent(deviceId) ?? "n/a",
                InteractionDiagnostics.LastPlaySessionNew(deviceId)?.ToString() ?? "n/a");
        }

        // JF-390 PreEnqueueOnStart (pre-compute): resolve the next track EARLY so
        // PlaybackNearlyFinished can respond with zero library lookups. This reduces
        // the server-side processing window on high-latency endpoints (Tailscale).
        // We cannot send AudioPlayer.Play from PlaybackStarted (platform rejects it);
        // we only cache the resolution result.
        if (_config.PreEnqueueOnStart && _libraryManager != null)
        {
            TryPrecomputeNext(req.Token, session, user, deviceId);
        }

        // JF-299: return a keep-alive ack (shouldEndSession=null). Amazon REJECTS
        // shouldEndSession=false on AudioPlayer event responses ("Response may not
        // have shouldEndSession set to false"), which raised InvalidResponse (and
        // "Qualcosa è andato storto") on every playback since commit 122d1fb. Audio
        // control intents (Pause/Resume/Next/Previous/Stop) are auto-routed by the
        // platform while audio plays, independent of this ack. See CLAUDE.md
        // "AudioPlayer event restrictions" and the feedback_should_end_session note.
        return Task.FromResult(BuildKeepAliveResponse());
    }

    /// <summary>
    /// Reports playback start to the Jellyfin server as a fire-and-forget side effect with
    /// elapsed-time logging. The method runs concurrently with the response (only its
    /// synchronous prefix, up to the first await inside OnPlaybackStart, executes before
    /// HandleAsync returns). Exceptions are swallowed after logging: a failed report must
    /// not surface to the user (the next playback event re-reports position anyway).
    /// JF-425: the server's session write lands inside OnPlaybackStart after an internal
    /// await, so this report can complete long after a later stop already cleared the
    /// session. When that happens the superseding stop is re-issued to clear the
    /// resurrected Playing state (zombie position card, MediaInfo, resume fallback).
    /// </summary>
    /// <param name="playbackStartInfo">The playback-start report to send.</param>
    /// <param name="deviceId">The Alexa device ID (per-device ordering key).</param>
    private async Task ReportPlaybackStartAsync(PlaybackStartInfo playbackStartInfo, string deviceId)
    {
        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            await SessionManager.OnPlaybackStart(playbackStartInfo).ConfigureAwait(false);
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > SlowReportMs)
            {
                Logger.LogWarning(
                    "PlaybackStarted: server playback-start report took {ElapsedMs}ms for item {Token} (slow, but no longer blocks the Alexa response; JF-410)",
                    stopwatch.ElapsedMilliseconds, playbackStartInfo.ItemId);
            }
            else
            {
                Logger.LogDebug(
                    "PlaybackStarted: server playback-start report completed in {ElapsedMs}ms",
                    stopwatch.ElapsedMilliseconds);
            }

            PlaybackStopInfo? supersedingStop = PlaybackReportOrdering.GetSupersedingStop(deviceId);
            if (supersedingStop is not null)
            {
                Logger.LogWarning(
                    "PlaybackStarted: report for item {Token} completed after a newer playback stop (superseded while in flight); re-issuing the stop to clear the session (JF-425)",
                    playbackStartInfo.ItemId);
                await SessionManager.OnPlaybackStopped(supersedingStop).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "PlaybackStarted: server playback-start report (or its ordering correction) failed for item {Token}", playbackStartInfo.ItemId);
        }
    }

    /// <summary>
    /// Resolves the next sequential queue item, fetches its library metadata, and
    /// stores the result in <see cref="NextTrackPrecomputeCache"/> keyed by device +
    /// current track token. PlaybackNearlyFinishedEventHandler checks this cache first
    /// and, on a hit, skips the library lookups entirely (instant response).
    /// Deliberately sequential-only: shuffle, repeat, and radio/PostPlay resolution
    /// remain in NearlyFinished as the authoritative resolver.
    /// </summary>
    private void TryPrecomputeNext(string currentToken, SessionInfo session, Entities.User user, string deviceId)
    {
        if (session.NowPlayingQueue.Count == 0 || !Guid.TryParse(currentToken, out Guid currentId))
        {
            return;
        }

        int currentIndex = -1;
        for (int i = 0; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == currentId)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0 || currentIndex + 1 >= session.NowPlayingQueue.Count)
        {
            return;
        }

        Guid nextId = session.NowPlayingQueue[currentIndex + 1].Id;
        MediaBrowser.Controller.Entities.BaseItem? item = _libraryManager?.GetItemById(nextId);
        if (item == null)
        {
            return;
        }

        string streamUrl = GetStreamUrl(nextId.ToString(), user);

        NextTrackPrecomputeCache.Store(deviceId, currentToken, nextId, item, streamUrl);
        Logger.LogInformation(
            "PlaybackStarted: pre-computed next track='{NextItem}' for device={DeviceId} (current='{CurrentToken}', queue position {Position}/{QueueSize})",
            item.Name, deviceId, currentToken, currentIndex + 2, session.NowPlayingQueue.Count);
    }
}
