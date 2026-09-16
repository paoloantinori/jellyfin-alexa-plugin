using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using AlexaSession = Alexa.NET.Request.Session;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Base handler class to handle skill requests.
/// </summary>
/// <remarks>
/// Handlers are registered as DI singletons (Registrator registers each handler type as
/// BaseHandler), so they MUST be stateless: no per-request mutable instance fields, or
/// concurrent Alexa requests will race on them. Injected dependencies should be readonly.
/// Per-request state belongs in the session/request parameters, not handler fields.
/// </remarks>
public abstract class BaseHandler
{
    /// <summary>
    /// Fast-fail budget in milliseconds for the request-path session lookup (JF-477).
    /// The lookup previously ran with the full <see cref="RetryHelper.AlexaRequestTimeoutMs"/> budget;
    /// during a transient auth-DB hiccup it hung the whole 6s before the handler's first
    /// line could run, and the ~8s Alexa window expired with zero log output (live
    /// incident 2026-09-03 corr=40edec8a). A session lookup that cannot answer in 2s
    /// cannot serve the request anyway: against the 6s controller cancellation and the
    /// 8s device window, 2s still leaves room for the handler's actual work while capping
    /// the worst case at a coherent not-found response instead of a silent timeout.
    /// The lookup result is cached in <see cref="SessionReferenceCache"/>, so this
    /// budget is only paid on a cache miss. The budget bounds the WHOLE call because
    /// ResolveSessionAsync dispatches the delegate via Task.Run (the callee's
    /// synchronous prefix contains a blocking EF read that observes no token).
    /// </summary>
    private const int SessionLookupTimeoutMs = 2000;

    /// <summary>
    /// JF-465: the ONE Layer-1 warming-gate preamble (JF-419 family). While the index
    /// is present but still loading, this refuses the request at entry (throws
    /// <see cref="Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions.SkillWarmingUpException"/>,
    /// translated once by the request pipeline into the session-ending SkillWarmingUp
    /// Tell) instead of letting the handler fall to the cold database path that can
    /// exceed Alexa's ~8-second response window (live incident 2026-08-31 07:59).
    /// Call at handler entry, BEFORE the "searching" progressive response (no
    /// announcement-then-refusal) and AFTER any cancel-word escape hatch (an open
    /// Dialog flow must still cancel during warming). A null or disabled index
    /// degrades (no gate). Layer 2 (the ArtistSearch choke point) still covers every
    /// unguarded caller. The gated-handler roster is asserted by WarmingGateCoverageTests:
    /// gating a new handler requires adding it there.
    /// </summary>
    /// <param name="artistIndex">The artist index the handler's request path uses.</param>
    protected static void GuardIndexReady(IArtistIndex? artistIndex) => IndexWarmingGate.EnsureReady(artistIndex);

    /// <summary>
    /// The song n-gram overload of <see cref="GuardIndexReady(IArtistIndex)"/>: the
    /// per-path gate for handlers whose fast resource IS the song n-gram index.
    /// </summary>
    /// <param name="songIndex">The song n-gram index the handler's request path uses.</param>
    protected static void GuardIndexReady(ISongNgramIndex? songIndex) => IndexWarmingGate.EnsureReady(songIndex);

    private protected readonly PluginConfiguration _config;

    /// <summary>
    /// The playback-launch collaborator (JF-315 batch 4 + the batch-5 VideoApp launch
    /// family + the batch-9 announce speech pair): stream/image URL building,
    /// AudioLaunchSource resolution, the
    /// AudioPlayer.Play / VideoApp-for-audio response chokepoints, and the VideoApp
    /// launch family (launch builders, medium classification, progressive announce,
    /// the resume-aware video-launch announce speech),
    /// extracted from this class. COMPOSITION, not per-handler
    /// injection: each handler constructs its own instance here so the 61 handler
    /// ctors stay untouched; consume it via this inherited get-only property (AC#6's
    /// readonly-field consumption). The collaborator is stateless (config + logger +
    /// the progressive-send delegate), so the singleton-handler constraint this class
    /// documents is preserved.
    /// </summary>
    protected internal PlaybackLaunchBuilder Launch { get; }

    /// <summary>
    /// The search collaborator (JF-315 batch 6, census cluster E): the library-search
    /// and fuzzy-RECALL machinery (SafeGetItemsResult, SearchWithAsrFallbackAsync,
    /// FuzzyMatch/FuzzyMatchPhonetic, GetArtistSongsAsync,
    /// SearchItemsFuzzyAsync, GetSearchResponseMode), extracted from this class.
    /// (CachedSearchAsync moved with the batch and was later DELETED there,
    /// JF-315 batch 11's 6b decision: zero production callers.)
    /// COMPOSITION, not per-handler injection (the PlaybackLaunchBuilder precedent):
    /// each handler constructs its own instance here so the 61 handler ctors stay
    /// untouched; consume it via this inherited get-only property. Stateless (config +
    /// logger + the passed request budget). The auto-play decision block
    /// (<see cref="HandleFuzzyMiss"/>) deliberately STAYED here; rationale lives in
    /// the SearchService class doc.
    /// </summary>
    protected internal SearchService Search { get; }

    /// <summary>
    /// The cross-media-fallback collaborator (JF-315 batch 7, census cluster F): the
    /// shared entity-fallback gates (TryEntityFallbackAsync with the JF-363 band and
    /// the JF-382 downgrade, TrySongFallback, PassesArtistMatchAcceptance, the
    /// word-count guard, FindBestNonEmbeddedMatch) and the play-shape sinks they
    /// resolve through (BuildArtistSongsResponseAsync, BuildSingleSongResponse,
    /// ApplyAnnouncement), extracted from this class. COMPOSITION, not per-handler
    /// injection (the SearchService precedent): each handler constructs its own
    /// instance here so the 61 handler ctors stay untouched; consume it via this
    /// inherited get-only property. Stateless (config + logger + the Launch
    /// collaborator + the passed request budget). Lives in the Handler namespace
    /// (not Util) because its offer ask is wired into DisambiguationHelper;
    /// rationale in the CrossMediaFallback class doc.
    /// </summary>
    protected internal CrossMediaFallback CrossMedia { get; }

    /// <summary>
    /// The album/playlist-play collaborator (JF-315 batch 8, census cluster G): the
    /// ONE album query shape (BuildAlbumQuery), the JF-469 calling-word strip, the
    /// JF-345 song-to-album cascade (TryAlbumFallbackAsync), the ONE album play flow
    /// (BuildAlbumPlayResponseAsync), and the shared playlist play flow
    /// (BuildPlaylistPlayResponseAsync), extracted from this class. COMPOSITION, not
    /// per-handler injection (the SearchService precedent): each handler constructs
    /// its own instance here so the 61 handler ctors stay untouched; consume it via
    /// this inherited get-only property. Stateless (config + logger + the
    /// Launch/Search/CrossMedia collaborators + the passed request budget + the
    /// fuzzy-miss delegate; see the AlbumPlayService class doc for the JF-408
    /// stay that delegate preserves). Lives in the Handler namespace (the
    /// CrossMediaFallback precedent: like that collaborator's offer ask, this
    /// family's playlist flow resolves disambiguation through
    /// DisambiguationHelper, which lives in the same namespace).
    /// </summary>
    protected internal AlbumPlayService AlbumPlay { get; }

    /// <summary>
    /// The playback-progress reporting collaborator (JF-315 batch 10, census cluster
    /// H): the JF-522 position-composition pair
    /// (ComposeItemAbsolutePosition/ComposeEventPositionTicks with the fail-open
    /// runtime guard), the SessionManager progress writers (ReportPlaybackProgress,
    /// ApplyRepeatModeAsync, ReportStopOrderedAsync), GetPostPlayBehavior, and the
    /// static MirrorQueueToSession, extracted from this class. COMPOSITION, not
    /// per-handler injection (the PlaybackLaunchBuilder precedent): each handler
    /// constructs its own instance here so the 61 handler ctors stay untouched;
    /// consume it via this inherited get-only property. The real dependencies are
    /// ctor-passed (this handler's SessionManager, config, logger, and the Launch
    /// collaborator); stateless beyond those, so the singleton-handler constraint
    /// is preserved.
    /// </summary>
    protected internal ProgressReporter Progress { get; }

    /// <summary>
    /// The radio-track source collaborator (JF-315 batch 10, census cluster H):
    /// the item-seeded and genre-seeded similar-track queries
    /// (FindRadioTracksAsync/FindRadioTracksByGenreAsync) the radio queues are
    /// built from, extracted from this class. COMPOSITION, not per-handler
    /// injection (the PlaybackLaunchBuilder precedent); stateless (logger + the
    /// composition-passed request budget). The Fisher-Yates Shuffle family the
    /// radio queues consume moved to the static <see cref="Util.Shuffler"/> the
    /// same batch.
    /// </summary>
    protected internal RadioTrackSource Radio { get; }

    /// <summary>
    /// The TV next-up collaborator (JF-315 batch 11, census cluster K's TV trio):
    /// the shared series-by-name resolution, the Jellyfin NextUp query core, and
    /// the next-up episode launch (JF-324 latest-episode fallback + resume-aware
    /// announce), extracted from this class. Consumed by the two TV-episode
    /// handlers (PlayEpisode, PlayNextEpisode). COMPOSITION, not per-handler
    /// injection (the PlaybackLaunchBuilder precedent): each handler constructs
    /// its own instance here so the handler ctors stay untouched; consume it
    /// via this inherited get-only property. Stateless (logger + the
    /// Search/Launch collaborators + the composition-passed request budget).
    /// Lives in the Handler namespace (the ProgressReporter precedent: the series
    /// resolution gates through <see cref="FilterByContentAccess"/>, in-namespace).
    /// </summary>
    protected internal TvNextUpService TvNextUp { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">The logger factory instance.</param>
    protected BaseHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
    {
        SessionManager = sessionManager;
        _config = config;
        Logger = loggerFactory.CreateLogger<BaseHandler>();
        Search = new SearchService(config, Logger, RetryHelper.AlexaRequestTimeoutMs);
        // The method group preserves virtual dispatch to the SendProgressiveResponse
        // overrides (the JF-501 test seam); mechanism documented at the builder's ctor.
        Launch = new PlaybackLaunchBuilder(config, Logger, SendProgressiveResponse);
        CrossMedia = new CrossMediaFallback(config, Logger, Launch, RetryHelper.AlexaRequestTimeoutMs);
        // The lambda closes the generic fuzzy-miss decision block over BaseItem, the
        // one shape the playlist flow calls (the SendProgressiveResponse delegate-seam
        // precedent; the JF-408 decision block itself STAYS here, see AlbumPlayService).
        AlbumPlay = new AlbumPlayService(
            config, Logger, Launch, Search, CrossMedia, RetryHelper.AlexaRequestTimeoutMs,
            (query, candidates, selector, matchExtractor, mediaType, locale, autoPlayFunc, user)
                => HandleFuzzyMiss(query, candidates, selector, matchExtractor, mediaType, locale, autoPlayFunc, user: user));
        // Progress takes this handler's SessionManager (the progress writers report
        // through it) and the already-built Launch (launch-base reads).
        Progress = new ProgressReporter(sessionManager, config, Logger, Launch);
        Radio = new RadioTrackSource(Logger, RetryHelper.AlexaRequestTimeoutMs);
        TvNextUp = new TvNextUpService(Logger, Search, Launch, RetryHelper.AlexaRequestTimeoutMs);
    }

    /// <summary>
    /// Gets or sets the session manager instance.
    /// </summary>
    protected ISessionManager SessionManager { get; set; }

    /// <summary>
    /// Gets or sets logger instance.
    /// </summary>
    protected ILogger Logger { get; set; }

    /// <summary>
    /// Handle a skill request by calling the class HandleAsync method and return a skill response.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public Task<SkillResponse> HandleRequestAsync(Request request, Context context, CancellationToken cancellationToken = default)
    {
        return HandleRequestAsync(request, context, (AlexaSession?)null, cancellationToken);
    }

    /// <summary>
    /// Handle a skill request with Alexa session attributes for disambiguation state.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="alexaSession">The Alexa session containing session attributes.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public async Task<SkillResponse> HandleRequestAsync(Request request, Context context, AlexaSession? alexaSession, CancellationToken cancellationToken = default)
    {
        // Voice-based identification takes priority over account linking so multi-user
        // households get the right library automatically when speaker recognition is active.
        string? personId = context.System?.Person?.PersonId;
        Entities.User? user = !string.IsNullOrEmpty(personId)
            ? _config.GetUserByPersonId(personId)
            : null;

        // Account linking via access token serves as the fallback for devices without speaker recognition.
        if (user == null)
        {
            if (!Guid.TryParse(context.System!.User!.AccessToken, out Guid userId))
            {
                return BuildUserNotFoundResponse(request);
            }

            user = _config.GetUserById(userId);
        }

        if (user == null)
        {
            Logger.LogError("User not found for access token or person ID");

            return BuildUserNotFoundResponse(request);
        }

        // JF-477: the session lookup runs on EVERY request of every dialog turn, and
        // Jellyfin's implementation queries the device/auth database per call. Serve it
        // from the live-reference cache when fresh; only a miss pays the lookup below.
        // TryGet also refuses entries whose shared per-device session was re-stamped by
        // another household profile since they were stored (expected-UserId check), so a
        // served hit is always attributed to the right Jellyfin user.
        string deviceId = context.System!.Device!.DeviceID;
        SessionInfo? session;
        if (SessionReferenceCache.TryGet(user.JellyfinToken, deviceId, out SessionInfo? cachedSession))
        {
            Logger.LogDebug("Session cache hit for device {DeviceId} (JF-477)", deviceId);
            session = cachedSession;
        }
        else
        {
            session = await ResolveSessionAsync(user.JellyfinToken, deviceId, cancellationToken).ConfigureAwait(false);
        }

        string serverUrl = _config.ServerAddress;

        if (session == null)
        {
            Logger.LogError("Session not found for user {UserId}", user.Id);
            return BuildSessionMissResponse(request, user, deviceId);
        }

        try
        {
            SkillResponse response = await HandleAsync(request, context, user, session, alexaSession?.Attributes, cancellationToken).ConfigureAwait(false);
            Plugin.Instance?.CircuitBreaker.RecordSuccess(serverUrl);
            return response;
        }
        catch (Exception ex) when (RetryHelper.IsTransient(ex, cancellationToken))
        {
            Plugin.Instance?.CircuitBreaker.RecordFailure(serverUrl, Logger);
            throw;
        }
    }

    /// <summary>
    /// Resolves the Jellyfin session on a cache miss with the JF-477 fast-fail budget:
    /// retry-with-backoff (bounded by <see cref="SessionLookupTimeoutMs"/> inside the
    /// retry helper) raced against the same budget, because the retry budget alone cannot
    /// cut a HANGING first call, which is the incident shape. On budget expiry the caller
    /// gets null and degrades to the existing session-not-found tell; the abandoned
    /// lookup keeps running and, when it eventually lands a live session, warm-fills the
    /// cache (full budget off the request path, where no user is waiting) so the retry a
    /// few seconds later is a cache hit. A successful lookup stores the live reference
    /// for the next request.
    /// </summary>
    /// <param name="token">The user's Jellyfin access token.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The live session, or null when the lookup missed or exceeded the budget.</returns>
    private async Task<SessionInfo?> ResolveSessionAsync(string? token, string deviceId, CancellationToken cancellationToken)
    {
        Task<SessionInfo?> lookup = StartSessionLookup(
            token, deviceId, "GetSessionByAuthToken", SessionLookupTimeoutMs, cancellationToken);

        SessionInfo? session;
        try
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(SessionLookupTimeoutMs);
            session = await lookup.WaitAsync(budgetCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget token fired, not the caller's: the request itself is still live,
            // so degrade coherently instead of eating the Alexa window.
            Logger.LogWarning(
                "Session lookup exceeded the {BudgetMs}ms fast-fail budget for device {DeviceId}; degrading to the not-found response (JF-477)",
                SessionLookupTimeoutMs,
                deviceId);
            WarmFillAbandonedLookupInBackground(lookup, token, deviceId);
            return null;
        }

        SessionReferenceCache.Store(token, deviceId, session);
        return session;
    }

    /// <summary>
    /// Starts one session lookup with the JF-477 dispatch shape, shared by the
    /// request-path resolution and the JF-506 launch pre-warm. Task.Run dispatches
    /// the WHOLE delegate off the calling thread (JF-477 review P1):
    /// GetSessionByAuthenticationToken's synchronous prefix contains a blocking EF
    /// read (UserManager.GetUserById, verified at v10.11.11) that observes no
    /// cancellation token, so without this hop a hang there is unreachable by the
    /// WaitAsync budget, the retry budget, and the controller token alike, and the
    /// request hangs exactly as in the live incident.
    /// </summary>
    /// <param name="token">The user's Jellyfin access token.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="operationName">Retry/logging label.</param>
    /// <param name="timeoutMs">The retry helper's total timeout budget.</param>
    /// <param name="cancellationToken">Cancellation token (None for background lookups).</param>
    /// <returns>The in-flight lookup task.</returns>
    private Task<SessionInfo?> StartSessionLookup(
        string? token, string deviceId, string operationName, int timeoutMs, CancellationToken cancellationToken)
        => RetryHelper.ExecuteWithRetryAsync(
            () => Task.Run(() => SessionManager.GetSessionByAuthenticationToken(
                token, deviceId, Plugin.Instance!.Configuration.ServerAddress)),
            Logger,
            operationName,
            cancellationToken: cancellationToken,
            timeoutMs: timeoutMs);

    /// <summary>
    /// Awaits an abandoned (budget-expired) session lookup and, when it eventually
    /// completes, stores the result so the NEXT request is a cache hit (the warm/refill
    /// path may spend the full budget because no user is waiting on it). Self-protecting:
    /// it catches its own exceptions so the fire-and-forget task can never fault.
    /// </summary>
    /// <param name="lookup">The still-running lookup task.</param>
    /// <param name="token">The user's Jellyfin access token.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <returns>A task representing the observation and warm-fill.</returns>
    private async Task WarmFillAbandonedLookupAsync(Task<SessionInfo?> lookup, string? token, string deviceId)
    {
        try
        {
            SessionInfo? late = await lookup.ConfigureAwait(false);
            if (late != null)
            {
                SessionReferenceCache.Store(token, deviceId, late);
                Logger.LogDebug("Late session lookup landed for device {DeviceId}; cache warm-filled (JF-477)", deviceId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Abandoned session lookup faulted for device {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// CA2025 ownership boundary: the abandoned lookup task (a Task, which is
    /// IDisposable) is passed to the fire-and-forget warm-fill ONLY here, in a body
    /// with no disposal of its own. ResolveSessionAsync's budget CTS is disposed in
    /// that method on every exit path, and the analyzer cannot prove the background
    /// task's captures are disjoint from it, so the unawaited start is confined to
    /// this helper. After this call the background task alone observes the lookup.
    /// </summary>
    /// <param name="lookup">The still-running lookup task.</param>
    /// <param name="token">The user's Jellyfin access token.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    private void WarmFillAbandonedLookupInBackground(Task<SessionInfo?> lookup, string? token, string deviceId)
        => RunFireAndForget(WarmFillAbandonedLookupAsync(lookup, token, deviceId), "SessionLookupWarmFill");

    /// <summary>
    /// Determines whether this instance can handle the skill request.
    /// </summary>
    /// <param name="request">The Request type what this handler can process.</param>
    /// <returns>True if this handle can handle the given request type, false otherwise.</returns>
    public abstract bool CanHandle(Request request);

    /// <summary>
    /// Handle a skill request and return a skill response.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public abstract Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken);

    /// <summary>
    /// Handle a skill request with session attributes for disambiguation state.
    /// By default delegates to the session-unaware overload. Handlers that need
    /// session attributes (e.g. Yes/No during disambiguation) should override this.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="sessionAttributes">Session attributes from the Alexa request, or null.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public virtual Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        return HandleAsync(request, context, user, session, cancellationToken);
    }

    /// <summary>
    /// Build a pause response: AudioPlayer.Stop with session ended.
    /// Alexa routes resume to the skill automatically when audio was playing.
    /// </summary>
    // The literal locale is unread on this path: the open-session strings load
    // only when keepSessionOpen is true, which this overload never passes.
    public static SkillResponse BuildPauseResponse() => BuildPauseResponse(keepSessionOpen: false, locale: "en-US");

    /// <summary>
    /// Build a pause response with explicit session handling (JF-482). The
    /// AudioPlayer.Stop directive is ALWAYS sent: audio must stop regardless of what
    /// happens to the session. <paramref name="keepSessionOpen"/>=true returns
    /// ShouldEndSession=false so bare follow-up commands stay in-skill (the
    /// PauseKeepsSession experiment retesting the JF-299-derived rule; JF-299's
    /// evidence was about play responses during active playback, a different
    /// contention point), and the open response also carries a minimal
    /// OutputSpeech plus a Reprompt (JF-488): the JF-482 device matrix saw the
    /// platform close the silent open session with EXCEEDED_MAX_REPROMPTS ~8s
    /// after the response, with an error beep. Whether the reprompt prevents
    /// that timeout is the unverified hypothesis the JF-488 device test decides;
    /// the device matrix re-ran clean WITH the reprompt on 2026-09-05 (no
    /// EXCEEDED_MAX_REPROMPTS, no beep, in-skill follow-up, exact-offset
    /// resume), so the default is now on. Only the PAUSE path may pass
    /// true: stop and cancel responses always end the session (JF-299 covers
    /// them, JF-482 does not retest them), and neither play responses nor any
    /// other session-ending response goes through this builder with the flag
    /// set. <paramref name="keepSessionOpen"/>=false stays byte-identical to the
    /// pre-JF-482 pause response: silent, no reprompt, session ended.
    /// </summary>
    /// <param name="keepSessionOpen">Whether the session stays open (ShouldEndSession=false).
    /// Audio stops in both modes; the open mode adds minimal speech and a reprompt.</param>
    /// <param name="locale">The request locale, used only for the open-session
    /// speech and reprompt strings.</param>
    public static SkillResponse BuildPauseResponse(bool keepSessionOpen, string locale)
    {
        var response = ResponseBuilder.AudioPlayerStop();
        response.Response.ShouldEndSession = !keepSessionOpen;
        if (keepSessionOpen)
        {
            // An open session carrying neither speech nor reprompt is the shape the
            // platform timed out on (JF-482 device matrix, test b). The speech is
            // deliberately minimal so it cannot mask the Stop directive; the
            // reprompt is the JF-488 hypothesis under device test.
            response.Response.OutputSpeech = new PlainTextOutputSpeech
            {
                Text = ResponseStrings.Get("PauseSessionSpeech", locale)
            };
            response.Response.Reprompt = new Reprompt(ResponseStrings.Get("PauseSessionReprompt", locale));
        }

        return response;
    }

    /// <summary>
    /// Build a keep-alive response that keeps the skill session alive without
    /// opening the mic. Used by AudioPlayer event handlers to allow subsequent
    /// events (e.g., PING from APL handleTick) to reach the backend.
    /// </summary>
    public static SkillResponse BuildKeepAliveResponse()
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = null
            }
        };
    }

    /// <summary>
    /// Whether the request is an Alexa EVENT/system request whose response may not
    /// carry outputSpeech, card, or reprompt (JF-507): every AudioPlayer event
    /// (PlaybackStarted/Finished/NearlyFinished/Stopped/Failed), SessionEndedRequest,
    /// and SystemExceptionRequest. Amazon rejects any other shape with INVALID_RESPONSE
    /// "The following directives are not supported: Response may not contain an
    /// outputSpeech" (live incident 2026-09-06 17:12:52, corr=e54b0532/1eab419e: the
    /// JF-477 session-lookup fast-fail degraded a PlaybackFailed event to the
    /// UserNotFound Tell). Shared with the controller's own degradation sites.
    /// </summary>
    /// <param name="request">The incoming skill request.</param>
    /// <returns>True when the response must be the empty keep-alive shape.</returns>
    public static bool IsEventRequest(Request request)
        => request is AudioPlayerRequest or SessionEndedRequest or SystemExceptionRequest;

    /// <summary>
    /// The user/session-resolution degradation in the legal shape for the request:
    /// event requests (<see cref="IsEventRequest"/>) get the empty keep-alive
    /// response; every other request gets the localized UserNotFound Tell (JF-507).
    /// </summary>
    /// <param name="request">The incoming skill request.</param>
    /// <returns>The degradation response.</returns>
    private SkillResponse BuildUserNotFoundResponse(Request request)
        => IsEventRequest(request)
            ? BuildKeepAliveResponse()
            : ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", GetLocale(request)));

    /// <summary>
    /// JF-527: the session-miss degradation, discriminated by evidence of a dead token.
    /// A Jellyfin server update (e.g. the 12.0 auto-update) invalidates per-user
    /// JellyfinTokens; those users previously heard the generic UserNotFound tell, which
    /// names no cause and no remedy. When the user HAS a token AND the device has a
    /// recorded previous play (DeviceQueueManager's persisted last-played item), the miss
    /// is a token that used to work: speak the actionable AccountRelinkRequired tell with
    /// a card pointing at the plugin settings page. Empty token or no play history keeps
    /// the existing UserNotFound tell. Event requests keep the keep-alive shape (JF-507)
    /// in every branch.
    /// </summary>
    /// <param name="request">The incoming skill request.</param>
    /// <param name="user">The resolved plugin user (user resolution succeeded; the session lookup missed).</param>
    /// <param name="deviceId">The Alexa device ID the request came from.</param>
    /// <returns>The degradation response.</returns>
    private SkillResponse BuildSessionMissResponse(Request request, Entities.User user, string deviceId)
    {
        if (IsEventRequest(request))
        {
            return BuildKeepAliveResponse();
        }

        bool hadPreviousPlay = Plugin.Instance?.DeviceQueueManager?.GetLastPlayedItemId(deviceId) != null;
        if (user.HasJellyfinToken && hadPreviousPlay)
        {
            Logger.LogInformation(
                "AccountRelink: dead Jellyfin token for user {UserId} on device {DeviceId} (token fingerprint {TokenFingerprint})",
                user.Id,
                deviceId,
                TokenFingerprint(user.JellyfinToken));

            string locale = GetLocale(request);
            string message = ResponseStrings.Get("AccountRelinkRequired", locale);
            SkillResponse response = ResponseBuilder.Tell(message);
            response.Response.Card = new StandardCard
            {
                Title = ResponseStrings.Get("AccountRelinkCardTitle", locale),
                Content = message
            };
            return response;
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", GetLocale(request)));
    }

    /// <summary>
    /// Non-reversible log fingerprint of a secret token: 12 hex chars of the SHA-256
    /// digest. Unlike a first-chars slice this leaks nothing about the token's content
    /// while still letting triage correlate repeated misses across requests (JF-527).
    /// </summary>
    /// <param name="token">The secret token.</param>
    /// <returns>The hex fingerprint.</returns>
    private static string TokenFingerprint(string token)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))[..12];

    /// <summary>
    /// Build a response that ends the skill session, causing APL documents to dismiss.
    /// Used when playback finishes and the queue is exhausted, or when the user stops playback.
    /// </summary>
    public static SkillResponse BuildEndSessionResponse()
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true
            }
        };
    }

    /// <summary>
    /// Build a Dialog.ElicitSlot response (context-preserving elicit): the session stays
    /// inside the target intent's dialog, so the user's next utterance fills the named
    /// slot and already-filled slots survive the round-trip. The directive's
    /// updatedIntent declares EVERY slot of the target intent (allSlotNames) because
    /// Amazon rejects a partial updatedIntent (live INVALID_RESPONSE 2026-08-28 21:17:
    /// "All slots must be defined when sending updated intent in the Dialog.ElicitSlot
    /// directive"). The target intent must be registered in the model's dialog.intents
    /// with elicitationRequired=false (manual dialog control, CLAUDE.md anti-pattern #9).
    /// </summary>
    /// <param name="intentName">The intent whose dialog the elicit continues.</param>
    /// <param name="slotToElicit">The slot the user's next utterance fills.</param>
    /// <param name="allSlotNames">Every slot of the target intent, for updatedIntent.</param>
    /// <param name="prompt">The spoken question (also used as the reprompt when no explicit reprompt is given: every current elicit repeats its question when the user stays silent).</param>
    /// <param name="reprompt">Optional richer reprompt (JF-474): the re-prompt spoken when the user stays silent, distinct from the first ask (e.g. naming concrete answer choices) while the first ask stays short.</param>
    /// <param name="sessionAttributes">Session state for a flow that owns state (e.g. FindSong's search state); leave null when the dialog lives Amazon-side.</param>
    /// <param name="activeFlowKeys">The calling flow's session keys (JF-398 mutual exclusion); pass none when the elicit owns no flow state of its own.</param>
    /// <returns>The elicitation response.</returns>
    protected static SkillResponse BuildElicitSlotResponse(
        string intentName,
        string slotToElicit,
        string[] allSlotNames,
        string prompt,
        string? reprompt = null,
        Dictionary<string, object>? sessionAttributes = null,
        params string[] activeFlowKeys)
    {
        var response = new SkillResponse
        {
            Version = "1.0",
            SessionAttributes = sessionAttributes,
            Response = new ResponseBody
            {
                ShouldEndSession = false,
                OutputSpeech = new PlainTextOutputSpeech { Text = prompt },
                Reprompt = new Reprompt(reprompt ?? prompt),
                Directives = new List<IDirective> { new ElicitSlotDirective(slotToElicit, intentName, allSlotNames) }
            }
        };

        ConversationalFlows.MarkOthersInactive(response, activeFlowKeys);
        return response;
    }

    /// <summary>
    /// The locale-keyed form of <see cref="BuildElicitSlotResponse"/> for handlers whose
    /// elicit owns no session state of its own (the dialog lives Amazon-side): resolves
    /// <paramref name="promptKey"/> in the request locale and elicits
    /// <paramref name="slotToElicit"/> inside <paramref name="intentName"/>'s dialog,
    /// declaring EVERY intent slot in updatedIntent (Amazon rejects a partial one, live
    /// INVALID_RESPONSE 2026-08-28 21:17: "All slots must be defined when sending
    /// updated intent in the Dialog.ElicitSlot directive"). The target intent must be
    /// registered in the model's dialog.intents with elicitationRequired=false (manual
    /// dialog control, CLAUDE.md anti-pattern #9). JF-398 write-time mutual exclusion:
    /// no OTHER flow's stale state may ride along, so callers pass no active flow keys.
    /// This is the one home for that lesson (was duplicated per handler, JF-442).
    /// </summary>
    /// <param name="promptKey">The locale response-string key for the spoken question.</param>
    /// <param name="locale">The request locale, for the prompt string.</param>
    /// <param name="slotToElicit">The slot the user's next utterance fills.</param>
    /// <param name="intentName">The intent whose dialog the elicit continues.</param>
    /// <param name="allSlotNames">Every slot of the target intent, for updatedIntent.</param>
    /// <returns>The elicitation response.</returns>
    protected static SkillResponse BuildDialogElicitResponse(
        string promptKey,
        string locale,
        string slotToElicit,
        string intentName,
        params string[] allSlotNames)
        => BuildElicitSlotResponse(
            intentName,
            slotToElicit,
            allSlotNames,
            ResponseStrings.Get(promptKey, locale));

    /// <summary>
    /// The shared elicitation-trap cancel hatch (JF-550 hoist; the regime FindSong
    /// hit live 2026-08-28): while a Dialog.ElicitSlot is open, Alexa captures the
    /// user's next utterance INTO the elicited slot instead of routing it, so a
    /// bare stop/cancel word arrives as a slot value with dialogState IN_PROGRESS.
    /// That is a cancel, not a search for an item named "stop"/"ferma". Returns
    /// the session-ending cancel response when that shape is present, else null.
    /// FindSong keeps its own wider hatch (session-state-gated with the
    /// force-route disjuncts, JF-445); every other elicit-opening handler uses
    /// this one.
    /// </summary>
    /// <param name="intentRequest">The incoming intent request.</param>
    /// <param name="locale">The request locale, for the cancel vocabulary.</param>
    /// <param name="handlerTag">Handler name for the log line.</param>
    /// <returns>The cancel Tell, or null when no cancel was captured.</returns>
    protected SkillResponse? BuildCancelDuringOpenElicit(IntentRequest intentRequest, string locale, string handlerTag)
    {
        if (Util.CancelWords.IsDialogInProgress(intentRequest) && Util.CancelWords.AnySlotIsCancelWord(intentRequest, locale))
        {
            Logger.LogInformation("{Handler}: captured cancel word during open elicit, ending flow", handlerTag);
            return ResponseBuilder.Tell(ResponseStrings.Get("FlowCancelled", locale));
        }

        return null;
    }

    /// <summary>
    /// Extract the locale from the request, defaulting to en-US if not available.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The locale string (e.g. "en-US", "it-IT").</returns>
    protected static string GetLocale(Request request)
    {
        return GetLocalePublic(request);
    }

    /// <summary>
    /// Extract the locale from the request, defaulting to en-US if not available.
    /// Public version accessible from pipeline interceptors.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The locale string (e.g. "en-US", "it-IT").</returns>
    public static string GetLocalePublic(Request request)
    {
        return string.IsNullOrEmpty(request.Locale) ? "en-US" : request.Locale;
    }

    /// <summary>
    /// Returns a "feature disabled" response if the flag is off, or null if enabled.
    /// Reads from live configuration so config page changes take effect immediately.
    /// </summary>
    protected SkillResponse? IfFeatureDisabled(Func<PluginConfiguration, bool> isEnabled, Request request)
    {
        var config = Plugin.Instance?.Configuration;
        if (config != null && !isEnabled(config))
        {
            Logger.LogInformation("Feature is disabled via configuration");
            return ResponseBuilder.Tell(ResponseStrings.Get("FeatureDisabled", GetLocale(request)));
        }

        return null;
    }

    /// <summary>
    /// Filters an array of BaseItemKind values to only include types whose media type category
    /// is enabled in configuration.
    /// CONTRACT (JF-466): an EMPTY result means every requested kind is disabled,
    /// and Jellyfin treats an empty (length 0) IncludeItemTypes as NO type filter,
    /// not as match-nothing (verified against Jellyfin 10.11.11,
    /// BaseItemRepository: `if (filter.IncludeItemTypes.Length == 0)` applies only
    /// the exclude filter). Callers must treat an empty result as a hard zero
    /// (skip the query) or gate the entry, never assign it to IncludeItemTypes.
    /// Protected internal (JF-315 batch 11, was protected): the TvNextUpService
    /// collaborator gates its series resolution through this filter; the member
    /// itself stays here with its many handler callers (the batch-8
    /// ResolveJellyfinUser precedent).
    /// </summary>
    protected internal static BaseItemKind[] FilterByContentAccess(BaseItemKind[] types)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return types;
        }

        var allowed = new List<BaseItemKind>(types.Length);
        foreach (var type in types)
        {
            if (IsTypeAllowed(type, config))
            {
                allowed.Add(type);
            }
        }

        return allowed.Count == types.Length ? types : allowed.ToArray();
    }

    private static bool IsTypeAllowed(BaseItemKind type, PluginConfiguration config)
    {
        return type switch
        {
            BaseItemKind.Audio or BaseItemKind.MusicAlbum or BaseItemKind.MusicArtist => config.MusicEnabled,
            BaseItemKind.Movie or BaseItemKind.Episode or BaseItemKind.Series => config.VideosEnabled,
            BaseItemKind.AudioBook => config.BooksEnabled,
            BaseItemKind.Playlist => true, // playlists are cross-type, always allowed
            _ => true // unknown types pass through
        };
    }

    /// <summary>
    /// Checks if a media type category is disabled and returns a localized response.
    /// Use in handlers whose whole payoff is one media type (JF-467 gates the music-only
    /// handlers PlaySong/PlayAlbum/FindSong/PlayMoodMusic at entry this way): place the
    /// call AFTER the empty-slot prompt (a disabled user with no slot still gets the
    /// slot prompt) and BEFORE the first library query and the "searching" announcement.
    /// Reads Plugin.Instance live, so a standard config API replacement takes effect
    /// without a restart. Logs the disable at Information itself: callers need no
    /// second log line (the IfFeatureDisabled call sites follow the same bare idiom).
    /// </summary>
    protected SkillResponse? IfMediaTypeDisabled(Func<PluginConfiguration, bool> isEnabled, Request request)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        if (!isEnabled(config))
        {
            Logger.LogInformation("Media type is disabled via configuration");
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", GetLocale(request)));
        }

        return null;
    }

    /// <summary>
    /// Applies per-user library filtering to a query by setting TopParentIds.
    /// Resolves CollectionFolder IDs to physical folder IDs for correct filtering.
    /// No-op when the user has no library restrictions configured, and when every
    /// included kind lives outside any media library (playlists, live-TV channels:
    /// Util.LibraryFilter.IsOutOfLibraryKind), where the filter could only return
    /// zero rows. When the filter IS applied to a query that includes MusicArtist,
    /// the items-by-name bypass is set automatically (Util.LibraryFilter, JF-456).
    /// </summary>
    protected static void ApplyLibraryFilter(InternalItemsQuery query, Entities.User? user, ILibraryManager libraryManager, ILogger? logger = null)
        => Util.LibraryFilter.ApplyLibraryFilter(query, user, libraryManager, logger);

    /// <summary>
    /// Send a progressive response to keep the Alexa session alive during long operations.
    /// Resets the 8-second timeout. Only works with IntentRequest/LaunchRequest.
    /// Uses the dedicated HttpClientProgressive (factory-backed, fresh per call with a 2s
    /// timeout) because ProgressiveResponse sets BaseAddress internally, which cannot be
    /// modified on an HttpClient that has already sent a request.
    /// </summary>
    /// <remarks>
    /// Two delivery contracts share this method. The SearchingMedia ping call sites run it
    /// FIRE-AND-FORGET (via <see cref="RunFireAndForget"/>) so the 50-200ms Alexa API
    /// round-trip never blocks the final handler response. The one sanctioned AWAITED caller
    /// is <see cref="PlaybackLaunchBuilder.SpeakVideoLaunchAnnounceAsync"/> (JF-501): the launch announce must be
    /// SPOKEN BEFORE the final launch response reaches the device, because Amazon only plays
    /// a progressive response that arrives before the full response; reverting that caller
    /// to fire-and-forget silently reintroduces the mid-sentence cut the awaited send fixed.
    /// The entire body is wrapped in try/catch so the returned <see cref="Task"/> can never
    /// fault (discarded tasks stay analyzer-clean via <see cref="RunFireAndForget"/>, which
    /// observes the result to avoid CA2012).
    /// </remarks>
    /// <param name="context">The Alexa context containing API access token.</param>
    /// <param name="request">The request containing the request ID.</param>
    /// <param name="message">The message to speak to the user.</param>
    /// <returns>True when the message was sent; false on ANY failure (network, auth, timeout,
    /// internal error caught below), so the awaited caller can fall back to the final
    /// response instead of losing the message.</returns>
    /// <remarks>Virtual as the JF-501 test seam: unit tests subclass concrete handlers
    /// and override this to capture the progressive speech without network I/O.</remarks>
    protected virtual async Task<bool> SendProgressiveResponse(Context context, Request request, string message)
    {
        Logger.LogDebug("SendProgressiveResponse: sending message={Message}", message);
        try
        {
            // JF-314: use the dedicated progressive client (factory-backed, fresh per call, 2s timeout)
            var progressiveResponse = new ProgressiveResponse(
                context.System.ApiAccessToken,
                request.RequestId,
                context.System?.ApiEndpoint ?? "https://api.amazonalexa.com",
                Plugin.HttpClientProgressive);
            await progressiveResponse.SendSpeech(message).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Best-effort: never propagate. Swallowing here guarantees the discarded Task at
            // fire-and-forget call sites can never fault (no unobserved-exception escalation);
            // the bool lets the awaited JF-501 caller fall back to the final-response announce.
            Logger.LogWarning(ex, "Failed to send progressive response");
            return false;
        }
    }

    /// <summary>
    /// Safely run a best-effort <see cref="Task"/> fire-and-forget without awaiting it.
    /// Attaches a continuation that observes the task's completion, which prevents
    /// CA2012 (unobserved task exceptions) and keeps the build analyzer-clean under
    /// TreatWarningsAsErrors + AllEnabledByDefault. The task is expected to already be
    /// self-protecting (its own try/catch), so the continuation only logs on the rare
    /// case where the task still faults despite that.
    /// </summary>
    /// <param name="task">The task to run without awaiting.</param>
    /// <param name="operationName">Optional label for diagnostic logging if the task faults.</param>
    protected void RunFireAndForget(Task task, string operationName = "FireAndForget")
    {
        // CA2007: ConfigureAwait(false) is the project convention for library code.
        // CA2012: observing via ContinueWith marks the exception as observed.
        task.ConfigureAwait(false)
            .GetAwaiter()
            .OnCompleted(() =>
            {
                if (task.IsFaulted)
                {
                    Logger.LogWarning(task.Exception, "{Operation} task faulted unexpectedly", operationName);
                }
            });
    }

    /// <summary>
    /// Execute a synchronous Jellyfin API call with retry logic and exponential backoff.
    /// Thin delegation (JF-572) to the ONE shared request-budget entry point on
    /// RetryHelper; kept as a protected member because every handler calls it by
    /// this name. The budget defaults to the single-sourced
    /// <see cref="RetryHelper.AlexaRequestTimeoutMs"/>.
    /// </summary>
    /// <typeparam name="T">The return type.</typeparam>
    /// <param name="operation">The synchronous operation to execute.</param>
    /// <param name="operationName">Name for logging (e.g. "GetItemsList").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    protected Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
        => RetryHelper.ExecuteWithRequestBudgetAsync(operation, Logger, operationName, cancellationToken: cancellationToken);

    /// <summary>
    /// Result of a fuzzy match attempt with suggestion support.
    /// Public (JF-315 batch 8, was protected): the AlbumPlayService fuzzy-miss
    /// delegate seam names this type in its public return tuple; the decision
    /// block itself stays here.
    /// </summary>
    public enum FuzzyMissOutcome
    {
        /// <summary>A close candidate was found and handled (returned as response).</summary>
        SuggestionHandled,
        /// <summary>No close candidate found; caller should handle "not found".</summary>
        NotFound
    }

    /// <summary>
    /// Handle the case when FuzzyMatch returns null. Checks config for behavior:
    /// - Confirm: returns "Did you mean X?" prompt via disambiguation session
    /// - AutoPlay: invokes playFunc with the closest match and returns an announcement response
    /// Returns (SuggestionHandled, response) when a suggestion was made, or (NotFound, null) when no close candidate exists.
    /// The auto-play delegate is async (JF-538) so video-launch sites can send the
    /// JF-501 progressive announce inside the play builder before the launch response
    /// is built; delegates that only record a side effect wrap their null result in
    /// <see cref="Task.FromResult{TResult}"/> and keep the exact shape they had.
    /// </summary>
    /// <remarks>
    /// STAY-NOTE (JF-315 batch 6): this member and <see cref="FuzzyMissOutcome"/>
    /// deliberately did NOT move to SearchService. Full rationale in the
    /// SearchService class doc (the auto-play DECISION block stays at its decision
    /// point per the JF-408 fuzzy-recall-vs-judgment-layers memory). The fact unique
    /// to this site: it is seam-coupled to <see cref="Launch"/>'s progressive
    /// announce below (SpeakVideoLaunchAnnounceAsync rides the virtual
    /// SendProgressiveResponse the ~15 test harnesses override), which a
    /// collaborator move would have to re-thread.
    /// </remarks>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="query">The original search query.</param>
    /// <param name="candidates">The full list of candidate items.</param>
    /// <param name="selector">Function to extract the display name from an item.</param>
    /// <param name="matchExtractor">Function to create disambiguation match list from the best candidate.</param>
    /// <param name="mediaType">The media type for disambiguation state.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <param name="autoPlayFunc">Optional async function to play the suggested item in AutoPlay mode; a null result (the pre-JF-538 null sentinel, now the Task's value) means the delegate only recorded a side effect.</param>
    /// <returns>A tuple indicating the outcome and optional response.</returns>
    protected async Task<(FuzzyMissOutcome Outcome, SkillResponse? Response)> HandleFuzzyMiss<T>(
        string query,
        IReadOnlyList<T> candidates,
        Func<T, string> selector,
        Func<T, List<(Guid Id, string Name)>> matchExtractor,
        string mediaType,
        string locale,
        Func<T, Task<SkillResponse>>? autoPlayFunc = null,
        Entities.User? user = null,
        Context? context = null,
        Request? request = null)
        where T : class
    {
        if (candidates == null || candidates.Count == 0)
        {
            Logger.LogDebug("HandleFuzzyMiss: no candidates for query={Query}", query);
            return (FuzzyMissOutcome.NotFound, null);
        }

        var bestWithScore = FuzzyMatcher.FindBestMatchWithScore(query, candidates, selector);

        if (bestWithScore == null || bestWithScore.Value.Item == null || bestWithScore.Value.Score < FuzzyMatcher.GetSuggestionThreshold(user))
        {
            Logger.LogDebug("HandleFuzzyMiss: query={Query}, candidates={CandidateCount}, best={BestMatch}, score={Score}, below suggestion threshold — not-found",
                query, candidates.Count,
                bestWithScore?.Item != null ? selector(bestWithScore.Value.Item) : "(null)",
                bestWithScore?.Score ?? 0);
            return (FuzzyMissOutcome.NotFound, null);
        }

        T best = bestWithScore.Value.Item;
        int score = bestWithScore.Value.Score;

        // High-confidence matches auto-accept regardless of FuzzyMatchBehavior.
        // Only borderline matches (SuggestionThreshold..DefaultThreshold) consult the per-user config.
        // Two-step judgment, not one effective bar: the AutoPlay disjunct admits
        // sub-threshold scores, so GetEffectiveThreshold does not apply here; the
        // no-qualifier bar's comment below records the composite.
        FuzzyMatchBehavior behavior = user?.FuzzyMatchBehavior ?? FuzzyMatchBehavior.Confirm;
        // JF-508: PartialRatio's sliding window cannot see word boundaries, so a short
        // query with one entirely-absent word can still cross the score bar: "soul
        // coffee" vs "Starfish & Coffee" scored 72 by aligning against the window
        // "sh & coffee" (3 char edits standing in for the whole missing word "soul";
        // device 2026-09-06 corr=269e622d auto-played it with the closest-match
        // announce). For short queries (<= 2 tokens after KeywordMatcher.Tokenize, the
        // same locale stop-word stripping the song search uses) the score-bar
        // auto-accept therefore additionally requires FULL keyword coverage: every
        // query token must appear in the best candidate's tokenized name. Partial
        // coverage falls through to the "did you mean" prompt below; one "yes" plays
        // it, so the cost of the extra turn is one word. 3+ word queries are unchanged
        // (each word carries less identifying meaning), and the explicit
        // FuzzyMatchBehavior.AutoPlay opt-in is deliberately NOT gated: the user asked
        // to never be prompted. JF-526: the predicate lives in
        // KeywordMatcher.HasFullKeywordCoverage (diacritic-insensitive, the ONE
        // definition) and is applied at the sibling auto-play sites too.
        bool autoAccept = (score >= FuzzyMatcher.GetDefaultThreshold(user)
                           && KeywordMatcher.HasFullKeywordCoverage(KeywordMatcher.Tokenize(query, locale), selector(best), locale))
            || (behavior == FuzzyMatchBehavior.AutoPlay && autoPlayFunc != null);

        if (autoAccept && autoPlayFunc != null)
        {
            Logger.LogDebug("HandleFuzzyMiss: query={Query}, best={BestMatch}, score={Score}, auto-accept=true — auto-playing",
                query, selector(best), score);
            SkillResponse? playResponse = await autoPlayFunc(best).ConfigureAwait(false);

            // autoPlayFunc may return null when the caller only uses it as a side-effect
            // to narrow the candidate list (e.g. PlayArtistSongsIntentHandler).
            if (playResponse == null)
            {
                return (FuzzyMissOutcome.SuggestionHandled, null);
            }

            // Near-exact or exact matches (score >= ContainmentScore) play directly
            // without the "closest match" qualifier; it would sound redundant. This is
            // deliberately the bare ContainmentScore, not the effective bar
            // FuzzyMatcher.GetEffectiveThreshold(user, ContainmentScore) would raise
            // it to: AutoPlay-mode entry above can bypass a raised user threshold, and
            // the qualifier keys off the score alone. In Confirm mode the two steps
            // compose into exactly that effective bar.
            if (score >= FuzzyMatcher.ContainmentScore)
            {
                return (FuzzyMissOutcome.SuggestionHandled, playResponse);
            }

            IOutputSpeech qualifier = SpeechBuilder.BuildOutputSpeech(
                "FuzzyAutoPlayAnnouncementSsml", "FuzzyAutoPlayAnnouncement", locale, selector(best), query);

            // JF-538 review finding: when the delegate's launch announce already rode the
            // progressive vehicle (directive-only play response, null OutputSpeech), the old
            // overwrite made the user hear BOTH the progressive now-playing announce and this
            // qualifier on the final response - and the qualifier was still exposed to the
            // fast-start player cut. Speak the qualifier progressively too (same JF-501
            // mechanism and guards; a failed send falls back to the final response, the
            // pre-JF-538 shape). Callers that do not pass context/request (audio paths, side
            // effect delegates) keep the classic overwrite untouched.
            if (playResponse.Response.OutputSpeech is null && context != null && request != null)
            {
                IOutputSpeech? fallback = await Launch.SpeakVideoLaunchAnnounceAsync(context, request, qualifier).ConfigureAwait(false);
                if (fallback == null)
                {
                    return (FuzzyMissOutcome.SuggestionHandled, playResponse);
                }

                qualifier = fallback;
            }

            playResponse.Response.OutputSpeech = qualifier;
            return (FuzzyMissOutcome.SuggestionHandled, playResponse);
        }

        // Confirm mode: "Did you mean X?"
        Logger.LogDebug("HandleFuzzyMiss: query={Query}, best={BestMatch}, score={Score}, candidates={CandidateCount} — disambiguating",
            query, selector(best), score, candidates.Count);
        var matches = matchExtractor(best) ?? new List<(Guid, string)>();
        SkillResponse response = SpeechBuilder.AskLocalized(
            "FuzzySuggestionPromptSsml", "FuzzySuggestionPrompt", "FuzzySuggestionReprompt", locale, query, selector(best));

        var matchInfos = matches.Select(m => new DisambiguationHelper.MatchInfo { Id = m.Id.ToString(), Name = m.Name }).ToList();
        response.SessionAttributes = DisambiguationHelper.BuildAttributes(matchInfos, 0, mediaType);

        // JF-398: activating the disambiguation flow supersedes any other flow's state.
        ConversationalFlows.MarkOthersInactive(response, ConversationalFlows.DisambiguationKeys);
        return (FuzzyMissOutcome.SuggestionHandled, response);
    }

    /// <summary>
    /// Find the most recently played item that has non-zero server-side progress
    /// (PlaybackPositionTicks > 0 and not marked as Played). Queries across the
    /// specified content types ordered by DatePlayed descending.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="libraryManager">The library manager for querying items.</param>
    /// <param name="userDataManager">The user data manager for progress lookup.</param>
    /// <param name="pluginUser">The plugin user for library access filtering.</param>
    /// <param name="contentTypes">The content types to search (e.g. Audio, Movie, Episode).</param>
    /// <param name="maxCandidates">Maximum items to scan (default 50).</param>
    /// <returns>The best resume candidate and its position ticks, or (null, 0) if none found.</returns>
    protected static (BaseItem? Item, long PositionTicks) FindLastPlayedItemWithProgress(
        JellyfinUser jellyfinUser,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Entities.User pluginUser,
        BaseItemKind[] contentTypes,
        ILogger? logger = null,
        int maxCandidates = 50)
    {
        // JF-466: an EMPTY IncludeItemTypes means "all kinds" to Jellyfin (verified
        // against 10.11.11 BaseItemRepository: length 0 applies no type filter), so
        // when content access disabled every requested kind the query must not run
        // at all, or it would return in-progress items of ANY type.
        if (contentTypes.Length == 0)
        {
            logger?.LogDebug("FindLastPlayedItemWithProgress: no content types allowed by configuration, skipping query");
            return (null, 0);
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = contentTypes,
            IsPlayed = false,
            MinDateLastSavedForUser = DateTime.UtcNow.AddDays(-30),
            Limit = maxCandidates,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, pluginUser, libraryManager, logger);

        IReadOnlyList<BaseItem> recentItems = libraryManager.GetItemList(query);

        logger?.LogDebug(
            "FindLastPlayedItemWithProgress: found {Count} recently-played items for user {UserId}",
            recentItems.Count, jellyfinUser.Id);

        foreach (BaseItem item in recentItems)
        {
            UserItemData? userData = userDataManager.GetUserData(jellyfinUser, item);
            if (userData == null || userData.PlaybackPositionTicks <= 0)
            {
                continue;
            }

            logger?.LogDebug(
                "FindLastPlayedItemWithProgress: found item '{Name}' ({Id}) with positionTicks={Ticks}",
                item.Name, item.Id, userData.PlaybackPositionTicks);

            return (item, userData.PlaybackPositionTicks);
        }

        logger?.LogDebug("FindLastPlayedItemWithProgress: no item with progress found");
        return (null, 0);
    }

    /// <summary>
    /// Resolves a Jellyfin user by ID and returns either the user or an error response.
    /// </summary>
    /// <param name="userManager">The user manager to look up the user from.</param>
    /// <param name="userId">The Jellyfin user ID to resolve.</param>
    /// <param name="locale">The locale for the error response string.</param>
    /// <returns>A tuple: use <see cref="JellyfinUser"/> when not null, otherwise return <see cref="SkillResponse"/>.</returns>
    /// <remarks>Protected internal (JF-315 batch 8): the AlbumPlayService playlist flow
    /// resolves its Jellyfin user through this helper; the member itself stays here
    /// with its many handler callers (40+ call sites across the intent handlers,
    /// cluster K).</remarks>
    protected internal static (JellyfinUser? User, SkillResponse? Error) ResolveJellyfinUser(
        IUserManager userManager,
        Guid userId,
        string locale)
    {
        JellyfinUser? user = userManager.GetUserById(userId);
        if (user == null)
        {
            return (null, ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", locale)));
        }

        return (user, null);
    }

    /// <summary>
    /// Extract the first artist name from an audio item, or null for non-audio items.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <returns>The first artist name, or null.</returns>
    protected static string? GetArtistSubtitle(MediaBrowser.Controller.Entities.BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.Audio.Audio a && a.Artists is { Count: > 0 })
        {
            return a.Artists[0];
        }

        return null;
    }
}
