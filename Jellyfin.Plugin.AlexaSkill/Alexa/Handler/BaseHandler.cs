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
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
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
    /// Alexa request timeout budget in milliseconds.
    /// Matches the CancellationTokenSource(TimeSpan.FromSeconds(6)) in AlexaSkillController.
    /// Passed to the SearchService collaborator at composition (JF-315 batch 6).
    /// </summary>
    private const int AlexaRequestTimeoutMs = 6000;

    /// <summary>
    /// Fast-fail budget in milliseconds for the request-path session lookup (JF-477).
    /// The lookup previously ran with the full <see cref="AlexaRequestTimeoutMs"/> budget;
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

    /// <summary>
    /// Reorder items so favorites appear first, then by personal rating descending
    /// within each group (favorites, non-favorites). Items without a rating keep
    /// their original relative order (stable sort).
    /// </summary>
    /// <param name="items">Items to reorder.</param>
    /// <param name="user">Jellyfin user for favorite and rating lookup.</param>
    /// <param name="userDataManager">User data manager for favorite/rating status.</param>
    /// <returns>Items sorted with favorites first and highest-rated within each group.</returns>
    protected static IReadOnlyList<BaseItem> FavoritesAndRatingsFirst(
        IReadOnlyList<BaseItem> items,
        Jellyfin.Database.Implementations.Entities.User user,
        IUserDataManager userDataManager)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var favorites = new List<(int Index, BaseItem Item, double? Rating)>();
        var rest = new List<(int Index, BaseItem Item, double? Rating)>(items.Count);
        bool anyRating = false;

        for (int i = 0; i < items.Count; i++)
        {
            BaseItem item = items[i];
            UserItemData? data = userDataManager.GetUserData(user, item);
            double? rating = data?.Rating;
            if (rating.HasValue)
            {
                anyRating = true;
            }

            bool isFavorite = data?.IsFavorite == true;

            var entry = (i, item, rating);
            if (isFavorite)
            {
                favorites.Add(entry);
            }
            else
            {
                rest.Add(entry);
            }
        }

        if (!anyRating)
        {
            return items;
        }

        List<BaseItem> result = new List<BaseItem>(items.Count);
        result.AddRange(SortByRating(favorites));
        result.AddRange(SortByRating(rest));
        return result;
    }

    private static IEnumerable<BaseItem> SortByRating(List<(int Index, BaseItem Item, double? Rating)> items)
    {
        return items.OrderByDescending(i => i.Rating ?? double.MinValue)
                    .ThenBy(i => i.Index)
                    .Select(i => i.Item);
    }

    private protected readonly PluginConfiguration _config;

    /// <summary>
    /// The playback-launch collaborator (JF-315 batch 4 + the batch-5 VideoApp launch
    /// family): stream/image URL building, AudioLaunchSource resolution, the
    /// AudioPlayer.Play / VideoApp-for-audio response chokepoints, and the VideoApp
    /// launch family (launch builders, medium classification, progressive announce),
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
    /// CachedSearchAsync, FuzzyMatch/FuzzyMatchPhonetic, GetArtistSongsAsync,
    /// SearchItemsFuzzyAsync, GetSearchResponseMode), extracted from this class.
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
        Search = new SearchService(config, Logger, AlexaRequestTimeoutMs);
        // The method group preserves virtual dispatch to the SendProgressiveResponse
        // overrides (the JF-501 test seam); mechanism documented at the builder's ctor.
        Launch = new PlaybackLaunchBuilder(config, Logger, SendProgressiveResponse);
        CrossMedia = new CrossMediaFallback(config, Logger, Launch, AlexaRequestTimeoutMs);
        // The lambda closes the generic fuzzy-miss decision block over BaseItem, the
        // one shape the playlist flow calls (the SendProgressiveResponse delegate-seam
        // precedent; the JF-408 decision block itself STAYS here, see AlbumPlayService).
        AlbumPlay = new AlbumPlayService(
            config, Logger, Launch, Search, CrossMedia, AlexaRequestTimeoutMs,
            (query, candidates, selector, matchExtractor, mediaType, locale, autoPlayFunc, user)
                => HandleFuzzyMiss(query, candidates, selector, matchExtractor, mediaType, locale, autoPlayFunc, user: user));
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
    /// JF-522 writer-side provenance, the ONE raw-to-item-absolute conversion shared by
    /// every playback-position writer: a device-reported offset counts the OUTPUT
    /// timeline of the stream that produced it, which for a transcode-routed launch
    /// starts at the stream's launch base, so the item-absolute position is
    /// <c>base + raw</c>, including a raw of 0 (a PlaybackStarted for a transcode
    /// launch carries directive offset 0 while the item genuinely plays from its
    /// base). Base 0 (raw-static launches, plain plays) and absent scopes (pre-deploy
    /// launches, cleared queue files) return the raw ticks unchanged: base 0 IS the
    /// item timeline, and an absent scope has no defensible base to add (persisting
    /// the raw value there is the conservative, pre-JF-522 behavior).
    /// Stale-base guard: when the item runtime is known, a composition STRICTLY past
    /// it is never persisted (the raw offset wins). A legitimate composition can land
    /// ON the runtime (a finish event reports the full remaining stream), but cannot
    /// pass it; past-runtime means the base no longer describes the stream that
    /// produced the offset (a same-item relaunch whose stop arrived late, the sleep
    /// re-issue corner, a promote that never paired).
    /// </summary>
    /// <param name="rawTicks">The device-reported offset in .NET ticks (stream-relative; 0 at a transcode stream's start).</param>
    /// <param name="launchBaseMs">The stream's ACTIVE launch base in milliseconds (0 when none).</param>
    /// <param name="runtimeTicks">The item's runtime in ticks when known, else null (guard skipped).</param>
    /// <param name="logLabel">Caller identity for the composition/guard log lines.</param>
    /// <returns>The item-absolute position in ticks.</returns>
    protected long ComposeItemAbsolutePosition(long rawTicks, long launchBaseMs, long? runtimeTicks = null, string logLabel = "PlaybackEvent")
    {
        if (launchBaseMs <= 0)
        {
            return rawTicks;
        }

        long baseTicks = launchBaseMs * TimeSpan.TicksPerMillisecond;
        long composed = rawTicks + baseTicks;
        if (runtimeTicks is > 0 && composed > runtimeTicks.Value)
        {
            Logger.LogInformation(
                "{Label}: composed item-absolute position of {ComposedTicks} ticks (launch base {BaseMs}ms + raw {RawTicks} ticks) passes the item runtime ({RuntimeTicks} ticks); a legitimate composition cannot, so a stale launch scope is in play; persisting the raw offset as the more conservative truth",
                logLabel, composed, launchBaseMs, rawTicks, runtimeTicks.Value);
            return rawTicks;
        }

        Logger.LogDebug(
            "{Label}: persisting item-absolute position {ComposedTicks} ticks (launch base {BaseMs}ms + raw {RawTicks} ticks)",
            logLabel, composed, launchBaseMs, rawTicks);
        return composed;
    }

    /// <summary>
    /// JF-522: the ONE event-writer entry point for converting a device-reported raw
    /// offset into the item-absolute position - read the stream's ACTIVE launch base,
    /// look the item runtime up (fail-open, only when a base exists) for the
    /// stale-base guard, and compose. Every playback-position writer calls this
    /// instead of hand-assembling the triplet, so the "guard only when a base exists"
    /// policy and the null-to-0 collapse live in one place. An unparseable item id
    /// (<see cref="Guid.Empty"/>) keeps the raw ticks (no scope can be recorded for
    /// it). Callers without a library manager (the mode-change progress reports) skip
    /// the runtime guard - their positions are transient PlayState writes.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (launch-scope key).</param>
    /// <param name="itemId">The codec-parsed item id the event's token names.</param>
    /// <param name="rawOffsetMs">The device-reported offset in milliseconds.</param>
    /// <param name="logLabel">Caller identity for the composition/guard log lines.</param>
    /// <param name="queueManager">The caller's queue manager, or null to use <c>Plugin.Instance</c>'s.</param>
    /// <param name="libraryManager">Optional library manager for the runtime guard; null skips it.</param>
    /// <returns>The item-absolute position in ticks.</returns>
    protected long ComposeEventPositionTicks(
        string? deviceId,
        Guid itemId,
        long rawOffsetMs,
        string logLabel,
        DeviceQueueManager? queueManager = null,
        ILibraryManager? libraryManager = null)
    {
        long launchBaseMs = itemId != Guid.Empty
            ? Launch.GetActiveLaunchBaseMs(deviceId, itemId.ToString(), queueManager) ?? 0
            : 0;
        long? runtimeTicks = launchBaseMs > 0 ? TryGetRuntimeTicksForGuard(libraryManager, itemId) : null;
        return ComposeItemAbsolutePosition(
            TimeSpan.FromMilliseconds(rawOffsetMs).Ticks, launchBaseMs, runtimeTicks, logLabel);
    }

    /// <summary>
    /// Fail-open runtime lookup feeding <see cref="ComposeItemAbsolutePosition"/>'s
    /// stale-base guard (JF-522): the guard is an advisory bound, so a library-manager
    /// failure must not kill an event handler before the keep-alive ack Amazon requires
    /// - it reads null (guard skipped) and logs.
    /// </summary>
    /// <param name="libraryManager">The library manager (null reads null).</param>
    /// <param name="itemId">The item whose runtime to read.</param>
    /// <returns>The item runtime in ticks, or null when unknown.</returns>
    protected long? TryGetRuntimeTicksForGuard(ILibraryManager? libraryManager, Guid itemId)
    {
        if (libraryManager == null || itemId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return libraryManager.GetItemById(itemId)?.RunTimeTicks;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Playback composition guard: runtime lookup failed for item {ItemId}; guard skipped", itemId);
            return null;
        }
    }

    /// <summary>
    /// Resume-aware video-launch announce: "Resuming X from Y" when the user has playback
    /// progress, else the now-playing announce. VideoApp.Launch cannot honor the offset, so
    /// this only informs the user where they left off (playback still starts from the beginning).
    /// The fresh-play announce (resumeTicks == 0) is suppressed when announceOn is false; the
    /// resume announce is always spoken (position info, not the now-playing readout).
    /// </summary>
    protected static IOutputSpeech? BuildVideoLaunchSpeech(BaseItem item, string locale, long resumeTicks, bool announceOn)
    {
        if (resumeTicks > 0)
        {
            return new PlainTextOutputSpeech(ResponseStrings.Get("ResumingVideo", locale, item.Name, ResumeMath.FormatPosition(resumeTicks)));
        }

        return SpeechBuilder.BuildNowPlayingSpeech(item.Name, locale, announceOn);
    }

    /// <summary>
    /// Resume-aware video-launch announce that fetches the playback position itself. Falls back
    /// to the (gated) now-playing announce if the deps are unavailable.
    /// </summary>
    protected static IOutputSpeech? BuildVideoLaunchSpeech(BaseItem item, string locale, IUserDataManager? userDataManager, Jellyfin.Database.Implementations.Entities.User? jellyfinUser, bool announceOn)
    {
        long resumeTicks = (userDataManager is not null && jellyfinUser is not null)
            ? (userDataManager.GetUserData(jellyfinUser, item)?.PlaybackPositionTicks ?? 0)
            : 0;
        return BuildVideoLaunchSpeech(item, locale, resumeTicks, announceOn);
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
    /// </summary>
    protected static BaseItemKind[] FilterByContentAccess(BaseItemKind[] types)
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
    /// JF-425/JF-447: the ONE stop-report sequence shared by the stop-shaped event
    /// handlers (PlaybackStopped/Finished/Failed). Registers the stop for correction
    /// duty (the displacement classification is folded into RecordStop, so a null
    /// registration means the stop displaces an already-replaced stream and must not
    /// correct anything), reports it to the server with the registration completed in a
    /// finally (a correcting start report waits for it instead of firing a concurrent
    /// duplicate), and restores the new track's session entry when the stop was a
    /// displacement (its own server-side write cleared the entry the new track owns).
    /// Callers that need the displacement flag BEFORE building the stop info (Stopped
    /// zeroes the saved position) classify early and may pass their own reason.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (per-device ordering key).</param>
    /// <param name="rawToken">The event's raw stream token, for the displacement classification.</param>
    /// <param name="stopInfo">The stop report to send (replayed verbatim as the correction).</param>
    /// <param name="displacementRestoreReason">Reason stamped into the classification and restore logs.</param>
    /// <returns>A task representing the report and, for a displacement, the restore.</returns>
    protected async Task ReportStopOrderedAsync(string deviceId, string? rawToken, PlaybackStopInfo stopInfo, string displacementRestoreReason)
    {
        Playback.PlaybackReportOrdering.StopRegistration? registration =
            Playback.PlaybackReportOrdering.RecordStop(deviceId, rawToken, stopInfo);

        bool isDisplacement = registration == null;
        if (isDisplacement)
        {
            Logger.LogDebug(
                "{Reason}: displacement detected, item={Token} but the device's latest start is a different item; not recording the stop",
                displacementRestoreReason, rawToken);
        }

        try
        {
            await SessionManager.OnPlaybackStopped(stopInfo).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            // JF-477: the session no longer exists in the SessionManager (it was removed
            // while our cached live reference kept pointing at it). Drop the device's
            // cached entries so the next request refetches (and Jellyfin re-registers)
            // instead of reusing the corpse, then preserve today's propagation.
            SessionReferenceCache.InvalidateDevice(deviceId);
            throw;
        }
        finally
        {
            registration?.MarkReportCompleted();
        }

        if (isDisplacement)
        {
            await Playback.PlaybackReportOrdering.RestoreCurrentStartAsync(
                SessionManager, deviceId, Logger, displacementRestoreReason).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Execute a synchronous Jellyfin API call with retry logic and exponential backoff.
    /// </summary>
    /// <typeparam name="T">The return type.</typeparam>
    /// <param name="operation">The synchronous operation to execute.</param>
    /// <param name="operationName">Name for logging (e.g. "GetItemsList").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    protected Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
    {
        return RetryHelper.ExecuteWithRetryAsync(operation, Logger, operationName, cancellationToken: cancellationToken, timeoutMs: AlexaRequestTimeoutMs);
    }

    /// <summary>
    /// Gets the effective post-play behavior for a user, falling back to the global default.
    /// Per-user setting (when explicitly set, i.e. non-null) takes precedence.
    /// </summary>
    protected PostPlayBehavior GetPostPlayBehavior(Entities.User? user)
    {
        if (user?.PostPlayBehavior is { } userBehavior)
        {
            Logger.LogDebug("PostPlayBehavior: user={UserId} mode={Mode} source=PerUser", user.Id, userBehavior);
            return userBehavior;
        }

        Logger.LogDebug("PostPlayBehavior: user={UserId} mode={Mode} source=GlobalDefault", user?.Id, _config.DefaultPostPlayBehavior);
        return _config.DefaultPostPlayBehavior;
    }

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
    /// Shuffle a list in place using Fisher-Yates algorithm.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    /// <param name="list">The list to shuffle.</param>
    protected static void Shuffle<T>(IList<T> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Create a shuffled copy of a read-only list.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    protected static List<T> ShuffleCopy<T>(IReadOnlyList<T> source)
    {
        var copy = source.ToList();
        Shuffle(copy);
        return copy;
    }

    /// <summary>
    /// Create a shuffled copy of a read-only list truncated to at most
    /// <paramref name="cap"/> entries. Shuffle happens before the cap, so the kept
    /// subset is random (the radio queues: a 20-track radio start, a 15-track
    /// continuation). Caps differ per caller, so the cap is a parameter.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    /// <param name="source">The list to shuffle and cap.</param>
    /// <param name="cap">The maximum number of entries to keep.</param>
    /// <returns>A shuffled list of at most <paramref name="cap"/> entries.</returns>
    protected static List<T> ShuffleAndCap<T>(IReadOnlyList<T> source, int cap)
    {
        List<T> copy = ShuffleCopy(source);
        if (copy.Count > cap)
        {
            copy.RemoveRange(cap, copy.Count - cap);
        }

        return copy;
    }

    /// <summary>
    /// Rebuilds <paramref name="session"/>'s <c>NowPlayingQueue</c> from a
    /// <see cref="Playback.DeviceQueue"/>'s current (possibly reshuffled) item
    /// order, preserving <c>PlaylistItemId</c> and other metadata on items that
    /// already exist. Used by the shuffle handlers so that
    /// <c>PlaybackNearlyFinishedEventHandler.ResolveNextItemId</c> advances
    /// through the shuffled order rather than the original one.
    /// Protected internal (JF-315 batch 8): the AlbumPlayService playlist flow calls
    /// it; the member itself stays here with its cluster-H family (JF-572's
    /// consolidation scope), alongside its ShuffleOn/ShuffleOff handler callers.
    /// </summary>
    /// <param name="queue">The device queue whose item order to mirror.</param>
    /// <param name="session">The Jellyfin session whose NowPlayingQueue to rebuild.</param>
    protected internal static void MirrorQueueToSession(Playback.DeviceQueue queue, SessionInfo session)
    {
        if (queue.ItemIds.Count == 0)
        {
            return;
        }

        // Index existing queue items by Id (first occurrence wins) so metadata
        // (e.g. PlaylistItemId) is retained. Playlists may contain duplicate
        // tracks, so ToDictionary would throw — use TryAdd instead.
        var existing = new Dictionary<Guid, QueueItem>();
        foreach (QueueItem q in session.NowPlayingQueue)
        {
            existing.TryAdd(q.Id, q);
        }

        var deviceIds = new HashSet<Guid>();
        var rebuilt = new List<QueueItem>(queue.ItemIds.Count);
        foreach (string id in queue.ItemIds)
        {
            if (Guid.TryParse(id, out Guid guid))
            {
                deviceIds.Add(guid);
                rebuilt.Add(existing.TryGetValue(guid, out QueueItem? qi)
                    ? qi
                    : new QueueItem { Id = guid });
            }
        }

        // Preserve any session items not represented in the device queue (e.g.
        // progressive-continuation tracks) so the playable queue never shrinks.
        foreach (QueueItem qi in session.NowPlayingQueue)
        {
            if (!deviceIds.Contains(qi.Id))
            {
                rebuilt.Add(qi);
            }
        }

        session.NowPlayingQueue = rebuilt;
    }

    /// <summary>
    /// Reports playback progress to Jellyfin so the session PlayState (and the
    /// dashboard UI) stays in sync with the plugin's view. Shared by the shuffle
    /// handlers, which differ only in the <paramref name="order"/> they report.
    /// </summary>
    /// <param name="session">The Jellyfin session to report on.</param>
    /// <param name="deviceId">The Alexa device ID (launch-scope key). NOT the session's
    /// own DeviceId, which is the constant "AlexaDevice" the controller authenticates
    /// under and never keys a launch scope (review JF-522).</param>
    /// <param name="itemId">The currently-playing item ID.</param>
    /// <param name="offsetMs">The current playback offset in milliseconds.</param>
    /// <param name="order">The playback order to report (Shuffle or Default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async progress report.</returns>
    protected async Task ReportPlaybackProgress(SessionInfo session, string deviceId, Guid itemId, long offsetMs, PlaybackOrder order, CancellationToken cancellationToken)
    {
        // JF-522: PlayState positions are item-absolute; the context-derived offset the
        // shuffle handlers pass composes with the playing stream's launch base (0 for
        // every non-transcode-routed queue, where this is a numeric no-op).
        long positionTicks = ComposeEventPositionTicks(deviceId, itemId, offsetMs, "PlaybackProgress");
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            RepeatMode = session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            PositionTicks = positionTicks,
            PlaybackOrder = order,
        };

        await SessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared body of the loop-mode intents (LoopOn/LoopOff/LoopSongOn, JF-450):
    /// attaches the given repeat mode to the currently playing item via
    /// <see cref="SessionManager"/> progress reporting. The intent can arrive from
    /// an open session with nothing playing: there is no item to attach the mode
    /// to, so the localized no-media tell is returned instead of throwing.
    /// </summary>
    /// <param name="request">The skill request (locale source for the no-media tell).</param>
    /// <param name="context">The context of the skill intent request (AudioPlayer token).</param>
    /// <param name="session">The session instance to report progress on.</param>
    /// <param name="mode">The repeat mode to apply.</param>
    /// <param name="label">Log label identifying the calling intent.</param>
    /// <returns>An empty response, or the no-media tell when nothing is playing.</returns>
    protected async Task<SkillResponse> ApplyRepeatModeAsync(Request request, Context context, SessionInfo session, RepeatMode mode, string label)
    {
        PlaybackState? requestState = context.AudioPlayer;

        Logger.LogDebug("{Label}: entered, token={Token}, offset={OffsetMs}ms", label, requestState?.Token, requestState?.OffsetInMilliseconds);

        // The intent can arrive from an open session with nothing playing: there is
        // no item to attach the repeat mode to. Composite sleep tokens
        // ("{guid}|sleep:{ticks}") must resolve too; raw Guid.TryParse fails them.
        if (requestState?.Token == null || !StreamTokenCodec.TryGetItemId(requestState.Token, out Guid itemId))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", GetLocale(request)));
        }

        long positionTicks = ComposeEventPositionTicks(
            context?.System?.Device?.DeviceID, itemId, requestState.OffsetInMilliseconds, "LoopMode");
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            PlaybackOrder = session.PlayState.PlaybackOrder,
            PositionTicks = positionTicks,
            RepeatMode = mode,
        };

        await SessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);

        return ResponseBuilder.Empty();
    }

    /// <summary>
    /// Find tracks with genres matching the given audio item.
    /// Returns deduplicated results excluding the current item.
    /// </summary>
    /// <param name="current">The current audio item to match genres from.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A list of similar tracks.</returns>
    protected async Task<IReadOnlyList<BaseItem>> FindRadioTracksAsync(
        MediaBrowser.Controller.Entities.Audio.Audio current,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
        => await FindRadioTracksByGenreAsync(
            current.Genres ?? Array.Empty<string>(),
            jellyfinUser,
            user,
            libraryManager,
            cancellationToken,
            current.Id).ConfigureAwait(false);

    /// <summary>
    /// Genre-seeded variant of <see cref="FindRadioTracksAsync"/> (JF-474): the identical
    /// radio-track query, seeded by genre WORDS captured from the station elicit instead
    /// of a playing item's Genres array. The Genres filter matches the server's cleaned
    /// genre names exactly (Jellyfin 10.11 BaseItemRepository: ItemValue CleanValue
    /// equality), so a spoken "jazz" matches the genre "Jazz" while a non-genre word
    /// matches nothing and the caller falls through to its not-found.
    /// </summary>
    /// <param name="genres">The genre names to seed the query from.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="user">The plugin user for library filtering.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <param name="excludeId">Optional item id to exclude from the results (the seeding item).</param>
    /// <returns>A deduplicated list of tracks in those genres.</returns>
    protected async Task<IReadOnlyList<BaseItem>> FindRadioTracksByGenreAsync(
        string[] genres,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken,
        Guid? excludeId = null)
    {
        var allResults = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        if (excludeId.HasValue)
        {
            seen.Add(excludeId.Value);
        }

        if (genres.Length > 0)
        {
            var genreQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                Genres = genres,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Limit = 50,
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(genreQuery, user, libraryManager, Logger);

            IReadOnlyList<BaseItem> byGenre = await RetryAsync(
                () => libraryManager.GetItemList(genreQuery),
                "GetRadioGenreTracks",
                cancellationToken).ConfigureAwait(false);

            foreach (BaseItem item in byGenre)
            {
                if (seen.Add(item.Id))
                {
                    allResults.Add(item);
                }
            }
        }

        return allResults;
    }

    /// <summary>
    /// Query recently played items from Jellyfin and return them as display items
    /// suitable for an APL carousel. Deduplicates by name (keeps first = most recent),
    /// applies per-user library filtering, and respects feature flags for media types.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library access and image URL generation.</param>
    /// <param name="libraryManager">The library manager for querying items.</param>
    /// <param name="config">Plugin configuration for feature flags and server address.</param>
    /// <returns>A list of display items (empty, never null).</returns>
    private protected static List<Apl.ListDisplayItem> GetRecentlyPlayedItems(
        JellyfinUser jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        PluginConfiguration config)
    {
        var itemTypes = new List<BaseItemKind>();
        if (config.MusicEnabled)
        {
            itemTypes.Add(BaseItemKind.Audio);
        }

        if (config.VideosEnabled)
        {
            itemTypes.Add(BaseItemKind.Movie);
            itemTypes.Add(BaseItemKind.Episode);
        }

        if (config.BooksEnabled)
        {
            itemTypes.Add(BaseItemKind.AudioBook);
        }

        if (itemTypes.Count == 0)
        {
            return new List<Apl.ListDisplayItem>();
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = itemTypes.ToArray(),
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 20,
            DtoOptions = new DtoOptions(true)
        };

        ApplyLibraryFilter(query, user, libraryManager);

        IReadOnlyList<BaseItem> recentItems = libraryManager.GetItemList(query) ?? Array.Empty<BaseItem>();

        var results = new List<Apl.ListDisplayItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BaseItem item in recentItems)
        {
            if (results.Count >= 10)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            // Deduplicate by name to avoid "Song X" appearing twice
            if (!seenNames.Add(item.Name))
            {
                continue;
            }

            string subtitle = Apl.AplHelper.GetSubtitle(item);
            string artUrl = new Uri(new Uri(config.ServerAddress), "Items/" + item.Id + "/Images/Primary?api_key=" + user.JellyfinToken).ToString();

            results.Add(new Apl.ListDisplayItem(
                item.Name,
                item.Id.ToString(),
                subtitle,
                artUrl));
        }

        return results;
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
    /// Resolves a series by spoken name for playback (JF-324): content-access-gated
    /// SearchTerm query with the per-user library filter, then the shared fuzzy
    /// fallback. Shared by PlayEpisodeIntentHandler and PlayNextEpisodeIntentHandler
    /// so the explicit season+episode path and the next-up paths match series the
    /// same way.
    /// </summary>
    /// <param name="libraryManager">The library manager for the series query.</param>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library access filtering.</param>
    /// <param name="seriesName">The spoken series name.</param>
    /// <param name="locale">The request locale for error response strings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matched series, or an error response (content disabled or series not found).</returns>
    protected async Task<(BaseItem? Series, SkillResponse? Error)> ResolveSeriesForPlaybackAsync(
        ILibraryManager libraryManager,
        JellyfinUser jellyfinUser,
        Entities.User user,
        string seriesName,
        string locale,
        CancellationToken cancellationToken)
    {
        // JF-466: an EMPTY IncludeItemTypes means "all kinds" to Jellyfin, so a
        // videos-disabled configuration must hard-zero here instead of querying.
        BaseItemKind[] seriesKinds = FilterByContentAccess(new[] { BaseItemKind.Series });
        if (seriesKinds.Length == 0)
        {
            Logger.LogInformation("Series resolution skipped: no series kind allowed by configuration");
            return (null, ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", locale)));
        }

        var seriesQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = seriesName,
            IncludeItemTypes = seriesKinds,
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(seriesQuery, user, libraryManager, Logger);
        Logger.LogDebug("ResolveSeries: querying Jellyfin with searchTerm='{SeriesName}', types=Series", seriesName);
        IReadOnlyList<BaseItem> seriesList = await RetryAsync(() => libraryManager.GetItemList(seriesQuery), "GetSeries", cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("ResolveSeries: Jellyfin returned {ResultCount} series", seriesList.Count);

        if (seriesList.Count > 0)
        {
            return (seriesList[0], null);
        }

        var fuzzy = await Search.SearchItemsFuzzyAsync(seriesName, jellyfinUser, user, libraryManager, seriesKinds, cancellationToken, "SeriesFuzzyFallback", locale: locale).ConfigureAwait(false);
        if (fuzzy != null)
        {
            return (fuzzy.Value.Item, null);
        }

        return (null, ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSeries", locale, seriesName)));
    }

    /// <summary>
    /// JF-324 shared NextUp query core: Jellyfin's per-user next-unwatched episodes of
    /// a series via <c>ITVSeriesManager.GetNextUp</c> (EnableResumable so an in-progress
    /// episode counts as the next one). INTENT-PATH core only (single candidate,
    /// <see cref="PlayNextUpEpisodeAsync"/>): PlaybackNearlyFinishedEventHandler's
    /// episode auto-advance deliberately does NOT use NextUp, because a
    /// SeriesId-scoped GetNextUp returns at most one item on Jellyfin 10.11 and on
    /// the event path that item is always the finishing episode itself; the event
    /// path queries the series' unplayed episodes directly instead (C1).
    /// Content and library gating stay in the caller: the intent path gates at series
    /// resolution (<see cref="ResolveSeriesForPlaybackAsync"/>).
    /// </summary>
    /// <param name="tvSeriesManager">The Jellyfin TV series manager (NextUp source).</param>
    /// <param name="jellyfinUser">The Jellyfin user (per-user watched state).</param>
    /// <param name="seriesId">The series to advance within.</param>
    /// <param name="seriesName">The series name (logging only).</param>
    /// <param name="limit">How many next-up candidates to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The next-up episodes in Jellyfin's order (possibly empty, never null).</returns>
    protected async Task<IReadOnlyList<BaseItem>> GetNextUpEpisodesAsync(
        ITVSeriesManager tvSeriesManager,
        JellyfinUser jellyfinUser,
        Guid seriesId,
        string? seriesName,
        int limit,
        CancellationToken cancellationToken)
    {
        var nextUpQuery = new NextUpQuery
        {
            User = jellyfinUser,
            SeriesId = seriesId,
            Limit = limit,
            EnableResumable = true
        };
        Logger.LogDebug("NextUp: querying NextUp for seriesId={SeriesId}, enableResumable=true, limit={Limit}", seriesId, limit);
        var nextUpSw = System.Diagnostics.Stopwatch.StartNew();
        QueryResult<BaseItem> nextUp = await RetryAsync(
            () => tvSeriesManager.GetNextUp(nextUpQuery, new DtoOptions(true)),
            "GetNextUp",
            cancellationToken).ConfigureAwait(false);
        nextUpSw.Stop();
        // Stage timing (device session 2026-09-06: four requests spent 6-26s between
        // these two lines while a remux and the startup catalog sync ran; controlled
        // re-runs under the same encode load measured 91-131ms, so the spikes were a
        // transient that left no trace. This line makes the next occurrence readable
        // from the logs instead of inferred).
        Logger.LogInformation(
            "NextUp: GetNextUp took {ElapsedMs}ms for series '{SeriesName}' ({ResultCount} results)",
            nextUpSw.ElapsedMilliseconds, seriesName, nextUp?.Items?.Count ?? 0);
        return nextUp?.Items ?? Array.Empty<BaseItem>();
    }

    /// <summary>
    /// JF-324 shared next-up episode launch: resolves the next unwatched episode of a
    /// series via Jellyfin's NextUp (per-user watched state; EnableResumable so an
    /// in-progress episode counts as the next one, which is what both "next episode"
    /// and "continue watching" mean to a viewer who stopped mid-episode), falls back to
    /// the most recently created episode when NextUp is empty (nothing unwatched left),
    /// and launches the winner via VideoApp with the resume-aware announce. Used by
    /// PlayNextEpisodeIntentHandler and PlayEpisodeIntentHandler's series-only
    /// fallback. Library and content gating happen in the caller's series resolution
    /// (<see cref="ResolveSeriesForPlaybackAsync"/>); this core only needs the
    /// already-scoped series.
    /// </summary>
    /// <param name="tvSeriesManager">The Jellyfin TV series manager (NextUp source).</param>
    /// <param name="libraryManager">The library manager (latest-episode fallback query).</param>
    /// <param name="userDataManager">The user data manager (resume-aware announce).</param>
    /// <param name="jellyfinUser">The Jellyfin user (per-user watched state).</param>
    /// <param name="user">The plugin user (stream URL + announce toggle).</param>
    /// <param name="session">The Jellyfin session (now-playing queue).</param>
    /// <param name="series">The already-resolved series item.</param>
    /// <param name="locale">The request locale for response strings.</param>
    /// <param name="context">The Alexa context (JF-505 screenless-device launch gate).</param>
    /// <param name="request">The skill request (JF-501 progressive announce vehicle).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The VideoApp launch response, or the localized NoNextEpisode Tell when the series has no playable episode.</returns>
    protected async Task<SkillResponse> PlayNextUpEpisodeAsync(
        ITVSeriesManager tvSeriesManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        BaseItem series,
        string locale,
        Context context,
        Request request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> nextUpEpisodes = await GetNextUpEpisodesAsync(
            tvSeriesManager, jellyfinUser, series.Id, series.Name, 1, cancellationToken).ConfigureAwait(false);
        BaseItem? episode = nextUpEpisodes.FirstOrDefault();
        bool latestFallback = episode == null;

        if (episode == null)
        {
            // Latest fallback (JF-324): with nothing unwatched left, serve the most
            // recently created episode (DateCreated, the same ordering PlayPodcast
            // uses for "newest episode") and announce it with the latest-episode
            // wording instead of refusing.
            var latestQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
                Limit = 1,
                DtoOptions = new DtoOptions(true)
            };
            IReadOnlyList<BaseItem> latest = await RetryAsync(
                () => libraryManager.GetItemList(latestQuery),
                "GetLatestEpisode",
                cancellationToken).ConfigureAwait(false);
            episode = latest?.FirstOrDefault();
        }

        if (episode == null)
        {
            Logger.LogDebug("NextUp: no next-up and no episodes for series '{SeriesName}' ({SeriesId})", series.Name, series.Id);
            return ResponseBuilder.Tell(ResponseStrings.Get("NoNextEpisode", locale, series.Name));
        }

        Logger.LogDebug(
            "NextUp: resolved episode '{EpisodeName}' ({EpisodeId}) for series '{SeriesName}', latestFallback={LatestFallback}",
            episode.Name, episode.Id, series.Name, latestFallback);

        session.NowPlayingQueue = new List<QueueItem> { new QueueItem { Id = episode.Id } };
        session.FullNowPlayingItem = episode;

        // A next-up episode with playback progress is a resume: the announce says so
        // (VideoApp.Launch cannot honor the offset; the position info is spoken only).
        long resumeTicks = userDataManager.GetUserData(jellyfinUser, episode)?.PlaybackPositionTicks ?? 0;
        IOutputSpeech? speech;
        if (resumeTicks > 0)
        {
            speech = BuildVideoLaunchSpeech(episode, locale, resumeTicks, Launch.GetAnnounceNowPlaying(user));
        }
        else
        {
            speech = Launch.GetAnnounceNowPlaying(user)
                ? SpeechBuilder.BuildOutputSpeech(
                    latestFallback ? "PlayingLatestEpisodeSsml" : "PlayingNextEpisodeSsml",
                    latestFallback ? "PlayingLatestEpisode" : "PlayingNextEpisode",
                    locale,
                    episode.Name)
                : null;
        }

        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        // JF-501: the announce is spoken progressively AFTER the source URL has resolved
        // (it is a call argument, so it evaluates first) and BEFORE the final launch
        // response returns, so the fast-start HLS player cannot cut it mid-sentence
        // (observed case).
        return await Launch.BuildVideoAppLaunchResponseAsync(
            context,
            request,
            locale,
            Launch.GetVideoAppLaunchUrl(episode, user),
            episode.Name,
            speech).ConfigureAwait(false);
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
