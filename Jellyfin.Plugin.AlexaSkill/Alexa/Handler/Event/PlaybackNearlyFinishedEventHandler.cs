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

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackNearlyFinishedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlaybackNearlyFinishedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackNearlyFinished;
    }

    /// <summary>
    /// Pre-fetch the next item in the queue and enqueue it for gapless playback.
    /// Handles loop modes (RepeatOne, RepeatAll), shuffle, and radio mode auto-population.
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
        if (!string.IsNullOrEmpty(currentToken) && currentToken.Contains("|sleep:", StringComparison.Ordinal))
        {
            int sleepIdx = currentToken.IndexOf("|sleep:", StringComparison.Ordinal);
            string deadlineStr = currentToken[(sleepIdx + "|sleep:".Length)..];
            if (long.TryParse(deadlineStr, out long deadlineTicks))
            {
                if (DateTimeOffset.UtcNow.UtcTicks >= deadlineTicks)
                {
                    Logger.LogInformation("Sleep timer expired, stopping playback");
                    return ResponseBuilder.Empty();
                }
            }
        }

        Guid? nextItemId = ResolveNextItemId(session, context);

        // If no next item and radio mode is on, auto-populate similar tracks
        if (nextItemId == null && RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID))
        {
            nextItemId = await AutoPopulateRadioTracks(session, cancellationToken).ConfigureAwait(false);
        }

        if (nextItemId == null)
        {
            Logger.LogDebug("No next item in queue, playback will end after current track");
            return ResponseBuilder.Empty();
        }

        // Pre-fetch the next item from the library to resolve metadata eagerly
        BaseItem? item = _libraryManager.GetItemById((Guid)nextItemId);
        if (item == null)
        {
            Logger.LogWarning("Next queue item {ItemId} not found in library", nextItemId);
            return ResponseBuilder.Empty();
        }

        string itemId = item.Id.ToString();

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

        IReadOnlyList<BaseItem> similar = await FindRadioTracksAsync(currentAudio, jellyfinUser, _libraryManager, cancellationToken).ConfigureAwait(false);

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
}
