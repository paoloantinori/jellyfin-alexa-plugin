using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaybackNearlyFinished events.
/// Pre-fetches and enqueues the next stream URL for gapless playback transitions.
/// Supports loop modes (RepeatOne replays the same track, RepeatAll wraps around),
/// shuffle order, and radio mode auto-population when the queue runs out.
/// </summary>
#pragma warning disable CA1711
public class PlaybackNearlyFinishedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackNearlyFinishedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlaybackNearlyFinishedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackNearlyFinished;
    }

    /// <summary>
    /// Pre-fetch the next item in the queue and enqueue it for gapless playback.
    /// Handles loop modes (RepeatOne, RepeatAll), shuffle, radio mode auto-population,
    /// and progressive queue continuation for large libraries.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A task representing the async operation.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        // Check for sleep timer deadline encoded in the current token
        string? currentToken = context.AudioPlayer?.Token;
        Logger.LogDebug(
            "PlaybackNearlyFinished: currentToken={Token}, offset={OffsetMs}ms",
            currentToken, context.AudioPlayer?.OffsetInMilliseconds);

        if (!string.IsNullOrEmpty(currentToken) && currentToken.Contains("|sleep:", StringComparison.Ordinal))
        {
            int sleepIdx = currentToken.IndexOf("|sleep:", StringComparison.Ordinal);
            string deadlineStr = currentToken[(sleepIdx + "|sleep:".Length)..];
            if (long.TryParse(deadlineStr, out long deadlineTicks))
            {
                if (DateTimeOffset.UtcNow.UtcTicks >= deadlineTicks)
                {
                    Logger.LogInformation("Sleep timer expired, stopping playback");
                    return BuildKeepAliveResponse();
                }
            }
        }

        // Progressive queue building: fetch more items if we're approaching the end
        TryFetchContinuationBatch(session, context);

        Guid? nextItemId = ResolveNextItemId(session, context);

        Logger.LogDebug(
            "PlaybackNearlyFinished: resolved next item={NextItemId}, loop={LoopMode}, shuffle={Shuffle}",
            nextItemId,
            session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            session.PlayState?.PlaybackOrder ?? PlaybackOrder.Default);

        // If no next item and radio mode is on, auto-populate similar tracks
        if (nextItemId == null && RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID))
        {
            nextItemId = await AutoPopulateRadioTracks(session, cancellationToken).ConfigureAwait(false);
        }

        if (nextItemId == null)
        {
            // Clean up continuation state when queue is exhausted
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);

            // PostPlay only when radio mode is NOT active.
            // Radio mode handles its own continuation; PostPlay is for single-track
            // playback that reaches queue exhaustion without radio.
            bool radioActive = RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID);
            if (!radioActive)
            {
                var postPlayMode = GetPostPlayBehavior(user);

                if (postPlayMode == PostPlayBehavior.AutoPlay)
                {
                    // AutoPlay: find similar tracks and enqueue for gapless transition.
                    // PlaybackNearlyFinished can return AudioPlayer.Play but NOT speech,
                    // so the music continues seamlessly without announcement.
                    string? currentItemId = context.AudioPlayer?.Token;
                    if (!string.IsNullOrEmpty(currentItemId))
                    {
                        nextItemId = await AutoPopulatePostPlayTracks(
                            currentItemId, session, user, context, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (nextItemId == null)
            {
                Logger.LogDebug("No next item in queue, playback will end after current track");
                return ResponseBuilder.Empty();
            }

            // PostPlay AutoPlay found tracks — fall through to enqueue below
        }

        // Pre-fetch the next item from the library to resolve metadata eagerly
        BaseItem? item = _libraryManager.GetItemById((Guid)nextItemId);
        if (item == null)
        {
            Logger.LogWarning("Next queue item {ItemId} not found in library", nextItemId);
            return ResponseBuilder.Empty();
        }

        string itemId = item.Id.ToString();

        // Update the device queue pointer for crash recovery
        if (_queueManager != null)
        {
            _queueManager.MoveTo(context.System.Device.DeviceID, itemId);

            // Also update the current position for resume-after-pause accuracy.
            // PlaybackNearlyFinished fires periodically, so this keeps the stored
            // position reasonably fresh even if PlaybackStopped doesn't fire.
            var queue = _queueManager.GetOrCreateQueue(context.System.Device.DeviceID);
            queue.CurrentItemId = itemId;
            if (context.AudioPlayer != null)
            {
                queue.CurrentPositionTicks = TimeSpan.FromMilliseconds(context.AudioPlayer.OffsetInMilliseconds).Ticks;
            }
        }

        // Use the optimized /stream?static=true endpoint for pre-fetched playback
        // (the original /universal endpoint adds an extra redirect hop)
        string audioUrl = GetStreamUrl(itemId, user);

        Logger.LogInformation(
            "Pre-fetching next track for gapless playback: {ItemName} ({ItemId}), loop={LoopMode}, shuffle={Shuffle}",
            item.Name,
            itemId,
            session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            session.PlayState?.PlaybackOrder ?? PlaybackOrder.Default);

        return BuildAudioPlayerResponse(PlayBehavior.Enqueue, audioUrl, itemId, item, user, context);
    }

    /// <summary>
    /// Check if the queue is running low and fetch more items from continuation state.
    /// This enables progressive queue building: the initial bulk-play handler fetches
    /// only the first few items, and this method lazily fetches the rest as needed.
    /// </summary>
    /// <param name="session">The current Jellyfin session.</param>
    /// <param name="context">The Alexa context for device identification.</param>
    private void TryFetchContinuationBatch(SessionInfo session, Context context)
    {
        QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        if (continuation == null)
        {
            return;
        }

        // Find current position in the queue
        Guid? currentItemId = session.FullNowPlayingItem?.Id;
        if (currentItemId == null && context.AudioPlayer?.Token != null
            && Guid.TryParse(context.AudioPlayer.Token, out Guid parsedToken))
        {
            currentItemId = parsedToken;
        }

        if (currentItemId == null)
        {
            return;
        }

        int currentIndex = -1;
        for (int i = 0; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == currentItemId.Value)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return;
        }

        // Only fetch when approaching the end of the current queue
        int remaining = session.NowPlayingQueue.Count - currentIndex - 1;
        if (remaining > ProgressiveQueueConstants.GetPrefetchThreshold())
        {
            return;
        }

        // Fetch the next batch
        IReadOnlyList<BaseItem> newItems = QueueContinuationFetcher.FetchNextBatch(
            continuation,
            _libraryManager,
            _userManager,
            Logger);

        if (newItems.Count == 0)
        {
            // No more items to fetch, remove continuation state
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
            return;
        }

        // Append new items to the queue (deduplicating)
        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var seen = new HashSet<Guid>(queue.Select(q => q.Id));

        if (continuation.Shuffle)
        {
            newItems = ShuffleCopy(newItems);
        }

        foreach (BaseItem item in newItems)
        {
            if (seen.Add(item.Id))
            {
                queue.Add(new QueueItem { Id = item.Id });
            }
        }

        session.NowPlayingQueue = queue;

        // Remove continuation if we've fetched everything
        if (continuation.StartIndex >= continuation.TotalCount)
        {
            QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
        }
    }

    /// <summary>
    /// Resolve the next item ID based on the current position in the queue,
    /// taking loop and shuffle modes into account.
    /// </summary>
    /// <param name="session">The current Jellyfin session with play state.</param>
    /// <param name="context">The Alexa context for current token.</param>
    /// <returns>The next item ID, or null if playback should end.</returns>
    private Guid? ResolveNextItemId(SessionInfo session, Context context)
    {
        RepeatMode repeatMode = session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone;
        PlaybackOrder playbackOrder = session.PlayState?.PlaybackOrder ?? PlaybackOrder.Default;

        if (session.NowPlayingQueue.Count == 0)
        {
            return null;
        }

        // Find current position in the queue
        Guid? currentItemId = session.FullNowPlayingItem?.Id;
        if (currentItemId == null && context.AudioPlayer?.Token != null
            && Guid.TryParse(context.AudioPlayer.Token, out Guid parsedToken))
        {
            currentItemId = parsedToken;
        }

        if (currentItemId == null)
        {
            return null;
        }

        int currentIndex = -1;
        for (int i = 0; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == currentItemId.Value)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return null;
        }

        // RepeatOne: replay the same track
        if (repeatMode == RepeatMode.RepeatOne)
        {
            return session.NowPlayingQueue[currentIndex].Id;
        }

        // Shuffle mode: pick a random next track from the queue (avoiding immediate repeat if possible)
        if (playbackOrder == PlaybackOrder.Shuffle && session.NowPlayingQueue.Count > 1)
        {
            int nextIndex;
            if (session.NowPlayingQueue.Count == 2)
            {
                nextIndex = currentIndex == 0 ? 1 : 0;
            }
            else
            {
                do
                {
                    nextIndex = Random.Shared.Next(session.NowPlayingQueue.Count);
                }
                while (nextIndex == currentIndex);
            }

            return session.NowPlayingQueue[nextIndex].Id;
        }

        // Sequential: advance to next item
        int nextPos = currentIndex + 1;

        // RepeatAll: wrap around to the beginning when reaching the end
        if (nextPos >= session.NowPlayingQueue.Count)
        {
            if (repeatMode == RepeatMode.RepeatAll)
            {
                nextPos = 0;
            }
            else
            {
                return null;
            }
        }

        return session.NowPlayingQueue[nextPos].Id;
    }

    /// <summary>
    /// Find similar tracks to the current item and append them to the queue.
    /// Returns the first new track ID, or null if no tracks found.
    /// </summary>
    private async Task<Guid?> AutoPopulateRadioTracks(SessionInfo session, CancellationToken cancellationToken)
    {
        var currentAudio = session.FullNowPlayingItem as MediaBrowser.Controller.Entities.Audio.Audio;
        if (currentAudio == null)
        {
            return null;
        }

        Jellyfin.Database.Implementations.Entities.User? jellyfinUser = _userManager.GetUserById(session.UserId);
        if (jellyfinUser == null)
        {
            return null;
        }

        Entities.User? pluginUser = _config.GetUserById(session.UserId);
        IReadOnlyList<BaseItem> similar = await FindRadioTracksAsync(currentAudio, jellyfinUser, pluginUser!, _libraryManager, cancellationToken).ConfigureAwait(false);

        if (similar.Count == 0)
        {
            Logger.LogInformation("Radio mode: no similar tracks found, ending radio");
            return null;
        }

        List<BaseItem> shuffled = similar.ToList();
        Shuffle(shuffled);
        if (shuffled.Count > 15)
        {
            shuffled.RemoveRange(15, shuffled.Count - 15);
        }

        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var seen = new HashSet<Guid>(queue.Select(q => q.Id));
        Guid? firstNewId = null;
        int addedCount = 0;

        foreach (BaseItem track in shuffled)
        {
            if (seen.Add(track.Id))
            {
                queue.Add(new QueueItem { Id = track.Id });
                firstNewId ??= track.Id;
                addedCount++;
            }
        }

        if (firstNewId != null)
        {
            session.NowPlayingQueue = queue;
            Logger.LogInformation("Radio mode: added {Count} similar tracks", addedCount);
        }

        return firstNewId;
    }

    /// <summary>
    /// Find similar tracks to the specified item and append them to the queue
    /// for PostPlay AutoPlay gapless transition. Enables RadioModeState for
    /// subsequent continuation via the existing AutoPopulateRadioTracks flow.
    /// </summary>
    private async Task<Guid?> AutoPopulatePostPlayTracks(
        string currentItemId,
        SessionInfo session,
        Entities.User user,
        Context context,
        CancellationToken cancellationToken)
    {
        BaseItem? item = _libraryManager.GetItemById(Guid.Parse(currentItemId));
        var currentAudio = item as MediaBrowser.Controller.Entities.Audio.Audio;
        if (currentAudio == null)
        {
            return null;
        }

        Jellyfin.Database.Implementations.Entities.User? jellyfinUser = _userManager.GetUserById(session.UserId);
        if (jellyfinUser == null)
        {
            return null;
        }

        Entities.User? pluginUser = _config.GetUserById(session.UserId);
        IReadOnlyList<BaseItem> similar = await FindRadioTracksAsync(
            currentAudio, jellyfinUser, pluginUser!, _libraryManager, cancellationToken).ConfigureAwait(false);

        if (similar.Count == 0)
        {
            Logger.LogInformation("PostPlay AutoPlay: no similar tracks found for {ItemName}", currentAudio.Name);
            return null;
        }

        List<BaseItem> shuffled = similar.ToList();
        Shuffle(shuffled);
        if (shuffled.Count > 15)
        {
            shuffled.RemoveRange(15, shuffled.Count - 15);
        }

        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var seen = new HashSet<Guid>(queue.Select(q => q.Id));
        Guid? firstNewId = null;
        int addedCount = 0;

        foreach (BaseItem track in shuffled)
        {
            if (seen.Add(track.Id))
            {
                queue.Add(new QueueItem { Id = track.Id });
                firstNewId ??= track.Id;
                addedCount++;
            }
        }

        if (firstNewId != null)
        {
            session.NowPlayingQueue = queue;
            RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);
            Logger.LogInformation("PostPlay AutoPlay: added {Count} similar tracks, radio mode enabled", addedCount);
        }

        return firstNewId;
    }
}
