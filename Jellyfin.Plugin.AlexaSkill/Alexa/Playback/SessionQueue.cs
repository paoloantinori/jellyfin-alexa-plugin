#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Shared lookup and append over the Jellyfin session's now-playing queue (JF-447
/// simplify: the linear index scan was copied inline across the playback event
/// handlers; JF-720: the append-unseen idiom followed). Lookup callers keep their
/// own current-item resolution policy (token-first vs now-playing-first) at the
/// call site; the append-unseen family is the ONE home of the copy + seen-set +
/// add-unseen + replace idiom the playback continuation paths share.
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
    /// The ids of every item currently in the session's now-playing queue: the
    /// ONE membership predicate the append-unseen family (<see cref="UnseenItems"/>
    /// derives, <see cref="AppendUnseen(SessionInfo, System.Collections.Generic.IEnumerable{QueueItem})"/> re-runs at commit) deduplicates
    /// against, so derive-time and commit-time dedup can never disagree on what
    /// "already queued" means (the JF-712 double-append race fix's soundness
    /// condition, made structural by JF-720).
    /// </summary>
    /// <param name="session">The session whose queue ids to collect.</param>
    /// <returns>A set of the queued item ids.</returns>
    internal static HashSet<Guid> IdSet(SessionInfo session)
        => new(session.NowPlayingQueue.Select(q => q.Id));

    /// <summary>
    /// JF-720: the DERIVE half of the append-unseen idiom, for the arms that must
    /// not write yet (the JF-712 derive-then-commit policy): returns the
    /// candidates not already queued, as fresh queue items, in candidate order.
    /// Pure: the session is only read. Both halves delegate to the ONE
    /// <see cref="TakeUnseen"/> core, so a population derived here is filtered by
    /// literally the same code the commit re-runs, and a future predicate edit
    /// lands in the core or nowhere.
    /// </summary>
    /// <param name="session">The session whose queue defines "already queued".</param>
    /// <param name="candidates">The candidate items, in the intended append order.</param>
    /// <returns>The unseen candidates as queue items; empty when all are queued.</returns>
    internal static List<QueueItem> UnseenItems(SessionInfo session, IEnumerable<BaseItem> candidates)
        => TakeUnseen(session, candidates.Select(QueueItemFor));

    /// <summary>
    /// JF-720: the COMMIT half of the append-unseen idiom (was three hand-rolled
    /// copies in PlaybackNearlyFinishedEventHandler): appends only the items
    /// whose ids are neither already queued nor earlier in the same batch, in one
    /// whole-list replace. Returns exactly the appended items, in order, AS THE
    /// GIVEN INSTANCES (a derived population commits verbatim); when nothing was
    /// appended the session's list instance is left untouched (the continuation
    /// fetch used to replace it with an equal-content copy unconditionally; the
    /// unified no-write-on-no-change shape keeps the unlocked window as small as
    /// the commit's). The dedup itself runs in the ONE <see cref="TakeUnseen"/>
    /// core shared with the derive half, so the re-run a sibling fire forces is
    /// literally the same code the derivation applied. UNLOCKED, like every
    /// inline copy before it (the JF-712 known race: the replace can lose a
    /// sibling's concurrent commit); a lock belongs HERE, the one place, if the
    /// shape ever bites live.
    /// </summary>
    /// <param name="session">The session whose queue the items append to.</param>
    /// <param name="items">The items to append, in order (duplicates within the batch land once).</param>
    /// <returns>The items actually appended, in order.</returns>
    internal static List<QueueItem> AppendUnseen(SessionInfo session, IEnumerable<QueueItem> items)
    {
        List<QueueItem> appended = TakeUnseen(session, items);
        if (appended.Count > 0)
        {
            var queue = new List<QueueItem>(session.NowPlayingQueue);
            queue.AddRange(appended);
            session.NowPlayingQueue = queue;
        }

        return appended;
    }

    /// <summary>
    /// BaseItem overload for the fetch-shaped sites (candidates the caller holds
    /// as items, not queue items): identical commit semantics, with the
    /// BaseItem-to-QueueItem construction kept INSIDE the family through the ONE
    /// <see cref="QueueItemFor"/> projection <see cref="UnseenItems"/> shares, so
    /// a field grown on QueueItem lands in one place instead of per call site.
    /// </summary>
    /// <param name="session">The session whose queue the items append to.</param>
    /// <param name="items">The candidate items, in order (duplicates within the batch land once).</param>
    /// <returns>The items actually appended, as queue items, in order.</returns>
    internal static List<QueueItem> AppendUnseen(SessionInfo session, IEnumerable<BaseItem> items)
        => AppendUnseen(session, items.Select(QueueItemFor));

    /// <summary>
    /// The ONE core of the append-unseen family: the unseen items of
    /// <paramref name="items"/>, in order, as the given instances, where "seen"
    /// starts as <see cref="IdSet"/> (already queued) and grows with each item
    /// taken (a duplicate within the batch lands once). The derive half calls it
    /// directly and writes nothing; the commit half calls it and appends. The
    /// JF-712 double-append race fix is sound only while derive-time and
    /// commit-time dedup compute the same predicate against the same store, and
    /// after the JF-720 extraction they share this one loop by construction.
    /// </summary>
    /// <param name="session">The session whose queue defines "already queued".</param>
    /// <param name="items">The candidate items, in order.</param>
    /// <returns>The unseen items, in order.</returns>
    private static List<QueueItem> TakeUnseen(SessionInfo session, IEnumerable<QueueItem> items)
    {
        var seen = IdSet(session);
        var unseen = new List<QueueItem>();
        foreach (QueueItem item in items)
        {
            if (seen.Add(item.Id))
            {
                unseen.Add(item);
            }
        }

        return unseen;
    }

    /// <summary>
    /// The ONE BaseItem-to-QueueItem projection of the append-unseen family (the
    /// derive half and the BaseItem append overload both construct queue items
    /// here, never at their call sites).
    /// </summary>
    /// <param name="item">The candidate item.</param>
    /// <returns>A queue item carrying the candidate's id.</returns>
    private static QueueItem QueueItemFor(BaseItem item) => new() { Id = item.Id };
}
