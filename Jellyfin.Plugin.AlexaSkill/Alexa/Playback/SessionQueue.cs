#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Shared lookup over the Jellyfin session's now-playing queue (JF-447 simplify: the
/// linear index scan was copied inline across the playback event handlers). Each caller
/// keeps its own current-item resolution policy (token-first vs now-playing-first) at
/// the call site; this helper only scans.
/// </summary>
internal static class SessionQueue
{
    /// <summary>
    /// Returns the zero-based position of <paramref name="itemId"/> in the session's
    /// now-playing queue (first match), or -1 when absent.
    /// </summary>
    /// <param name="session">The session whose queue to scan.</param>
    /// <param name="itemId">The item to locate.</param>
    /// <returns>The zero-based index, or -1 when the item is not queued.</returns>
    internal static int IndexOfQueueItem(SessionInfo session, Guid itemId)
    {
        for (int i = 0; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == itemId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The ids of every item currently in the session's now-playing queue, for
    /// deduplication checks against the live queue (the copy shared by the
    /// continuation fetch and the radio/PostPlay/episode-advance populates).
    /// </summary>
    /// <param name="session">The session whose queue ids to collect.</param>
    /// <returns>A set of the queued item ids.</returns>
    internal static HashSet<Guid> IdSet(SessionInfo session)
        => new(session.NowPlayingQueue.Select(q => q.Id));
}
