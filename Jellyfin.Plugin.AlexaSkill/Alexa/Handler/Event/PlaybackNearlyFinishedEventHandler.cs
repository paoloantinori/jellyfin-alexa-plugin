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
/// Enqueues the next item in the queue. When radio mode is enabled and the
/// queue is about to run out, automatically finds and appends similar tracks.
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
    /// Respond with next item in the queue. If radio mode is enabled and the
    /// queue is about to run out, auto-populate with similar tracks.
    /// </summary>
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
                    return ResponseBuilder.Empty();
                }
            }
        }

        Guid? nextItemId = null;
        for (int i = 0; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == session.FullNowPlayingItem?.Id)
            {
                if (i + 1 < session.NowPlayingQueue.Count)
                {
                    nextItemId = session.NowPlayingQueue[i + 1].Id;
                }

                break;
            }
        }

        // If no next item and radio mode is on, auto-populate similar tracks
        if (nextItemId == null && RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID))
        {
            nextItemId = await AutoPopulateRadioTracks(session, cancellationToken).ConfigureAwait(false);
        }

        if (nextItemId == null)
        {
            return ResponseBuilder.Empty();
        }

        BaseItem item = _libraryManager.GetItemById((Guid)nextItemId);
        if (item == null)
        {
            return ResponseBuilder.Empty();
        }

        string itemId = item.Id.ToString();
        string audioUrl = new Uri(new Uri(Plugin.Instance!.Configuration.ServerAddress), "Audio/" + itemId + "/universal").ToString();

        return BuildAudioPlayerResponse(PlayBehavior.Enqueue, audioUrl, itemId, item, user, context);
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

        Jellyfin.Database.Implementations.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);
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
