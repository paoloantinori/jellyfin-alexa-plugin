#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// JF-720 behavioral pins for the SessionQueue append-unseen family: the commit
/// half (<see cref="SessionQueue.AppendUnseen"/>) owns the copy + seen-set +
/// add-unseen + replace idiom all three PlaybackNearlyFinished sites used to
/// inline, and the derive half (<see cref="SessionQueue.UnseenItems"/>) is what
/// the derive-then-commit arms read. Two contracts matter beyond convenience:
/// the WITHIN-BATCH dedup and the conditional replace (an all-seen batch leaves
/// the session's list instance untouched, the unified shape the fetch site's
/// old unconditional replace did not have), and the DERIVE/COMMIT AGREEMENT
/// (the JF-712 double-append race fix is sound only while both halves compute
/// the same membership predicate against the same store; the agreement fact
/// below makes a predicate edit that touches one half but not the other RED,
/// which is the drift the JF-720 extraction exists to kill). The ROUTING of the
/// handler's sites to this family is pinned separately, structurally, by
/// PlaybackNearlyFinishedQueueWriteRosterTests.
/// </summary>
public class SessionQueueAppendUnseenTests
{
    private static SessionInfo CreateSession(params Guid[] queuedIds)
    {
        var sessionManager = new Mock<ISessionManager>();
        var session = TestHelpers.CreateTestSession(
            sessionManager.Object, LoggerFactory.Create(b => { }));
        session.NowPlayingQueue = queuedIds.Select(id => new QueueItem { Id = id }).ToList();
        return session;
    }

    private static QueueItem Item(Guid id) => new() { Id = id };

    [Fact]
    public void AppendUnseen_AppendsOnlyUnseenInOrder_AndReturnsTheGivenInstances()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid c = Guid.NewGuid();
        SessionInfo session = CreateSession(a, b);
        QueueItem candidate = Item(c);
        IReadOnlyList<QueueItem> originalList = session.NowPlayingQueue;

        List<QueueItem> appended = SessionQueue.AppendUnseen(session, new[] { Item(b), candidate });

        // Queue order: existing head preserved, unseen appended in candidate order.
        Assert.Equal(new[] { a, b, c }, session.NowPlayingQueue.Select(q => q.Id));
        // The defining COPY-AND-REPLACE shape (the ArtistIndexServiceTests/
        // SongNgramIndexServiceTests atomic-swap pin idiom): an appending commit
        // installs a NEW list instance and leaves the OLD one unmutated, so a
        // concurrent scanner holding the old reference (IndexOfQueueItem, IdSet)
        // never observes a half-appended queue. An in-place-mutation rewrite of
        // AppendUnseen (AddRange through the getter) passes every other fact and
        // must fail HERE.
        Assert.NotSame(originalList, session.NowPlayingQueue);
        Assert.Equal(2, originalList.Count);
        // The commit appends the GIVEN instances verbatim (a derived population
        // commits exactly what was derived; PlaylistItemId and future fields ride).
        Assert.Same(candidate, appended.Single());
        Assert.Same(candidate, session.NowPlayingQueue[^1]);
    }

    [Fact]
    public void AppendUnseen_DeduplicatesWithinTheBatch()
    {
        Guid a = Guid.NewGuid();
        Guid c = Guid.NewGuid();
        SessionInfo session = CreateSession(a);

        List<QueueItem> appended = SessionQueue.AppendUnseen(session, new[] { Item(c), Item(c) });

        Assert.Equal(new[] { a, c }, session.NowPlayingQueue.Select(q => q.Id));
        Assert.Single(appended);
    }

    [Fact]
    public void AppendUnseen_AllSeen_LeavesTheQueueInstanceUntouched()
    {
        Guid a = Guid.NewGuid();
        SessionInfo session = CreateSession(a);
        IReadOnlyList<QueueItem> originalList = session.NowPlayingQueue;

        List<QueueItem> appended = SessionQueue.AppendUnseen(session, new[] { Item(a) });

        Assert.Empty(appended);
        // The unified conditional replace: an all-seen batch performs NO write at
        // all (the fetch site used to swap in an equal-content copy; the commit
        // site already skipped). Keeps the unlocked window as small as it can be.
        Assert.Same(originalList, session.NowPlayingQueue);
    }

    [Fact]
    public void UnseenItems_ReturnsOnlyUnseenCandidates_InOrder()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid c = Guid.NewGuid();
        SessionInfo session = CreateSession(a);

        List<QueueItem> unseen = SessionQueue.UnseenItems(
            session, new[] { TestHelpers.CreateSong(id: a), TestHelpers.CreateSong(id: b), TestHelpers.CreateSong(id: a), TestHelpers.CreateSong(id: c) });

        Assert.Equal(new[] { b, c }, unseen.Select(q => q.Id));
        // Pure derive: the session queue is untouched.
        Assert.Equal(new[] { a }, session.NowPlayingQueue.Select(q => q.Id));
    }

    [Fact]
    public void UnseenItems_AndAppendUnseen_AgreeOnTheSameSessionState()
    {
        // The derive/commit agreement (the JF-712 soundness condition, JF-720's
        // reason to exist): for identical queue state and candidates, what the
        // derive half computes as new is exactly what the commit half appends.
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid c = Guid.NewGuid();
        SessionInfo deriveSession = CreateSession(a, b);
        SessionInfo commitSession = CreateSession(a, b);
        Audio[] candidates =
        {
            TestHelpers.CreateSong(id: a), TestHelpers.CreateSong(id: c),
            TestHelpers.CreateSong(id: b), TestHelpers.CreateSong(id: c)
        };

        List<QueueItem> derived = SessionQueue.UnseenItems(deriveSession, candidates);
        List<QueueItem> committed = SessionQueue.AppendUnseen(
            commitSession, candidates.Select(t => new QueueItem { Id = t.Id }));

        Assert.Equal(derived.Select(q => q.Id), committed.Select(q => q.Id));
        Assert.Equal(
            new[] { a, b }.Concat(derived.Select(q => q.Id)),
            commitSession.NowPlayingQueue.Select(q => q.Id));
    }
}
