#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-720 item 2: the WRITER-side roster for the JF-712 derive-then-commit policy
/// in <see cref="PlaybackNearlyFinishedEventHandler"/> (the SessionQueueReader /
/// DeliveredLaunchStateWrite IlCallScanner idiom, scoped to ONE handler the way
/// CaptureRefreshPairingTests pins one pairing). The behavioral pins
/// (PlaybackNearlyFinishedRefusalTests) prove the existing arms leave the right
/// state; this roster makes the EXCEPTION LIST structural: the handler performs
/// zero direct NowPlayingQueue assignments, its ENTIRE outbound call surface
/// into the SessionQueue helper family is the documented roster (the three
/// append-unseen sites: TryFetchContinuationBatch's pre-build fetched-batch
/// append BY DESIGN, DeriveSimilarTracksPopulation's derive-only UnseenItems
/// read, CommitPendingContinuation's post-build AppendUnseen commit point; plus
/// the reader legs IndexOfQueueItem x2 and the episode arm's IdSet skip-set),
/// and its one queue-affecting call outside the family is the entry rehydration
/// mirror HandleAsync reaches through
/// ProgressReporter.TryRehydrateSessionQueueFromDevice (BY DESIGN, JF-574). A
/// future queue write therefore fails SOMETHING no matter how it is shaped: a
/// raw assignment fails the setter fact, a call to ANY SessionQueue member
/// (including one that does not exist today) from any undocumented site fails
/// the surface fact, and an inlined loop fails both. The class-doc policy
/// cannot rot silently.
/// SELF-RED: a raw NowPlayingQueue assignment added anywhere in the handler
/// flips the setter fact; re-inlining the idiom at any of the three sites
/// flips BOTH the setter and surface facts (proven against this tree, probes
/// A and B); a new SessionQueue member called from the handler flips the
/// surface fact alone.
/// ACCEPTED BOUNDARIES (what this scan cannot see, the roster idiom's
/// documented-limit class):
/// 1. IN-PLACE MUTATION: the write probe is the NowPlayingQueue SETTER. Mutating
///    the queue through the getter's list reference (session.NowPlayingQueue.Add)
///    writes the store with no setter call and is invisible here; the guarantee
///    covers the house copy-and-replace shape only.
/// 2. OTHER-TYPE DELEGATION: queue mutations performed inside OTHER types are
///    outside this handler-local scan except the one pinned rehydration leg; the
///    surface fact sees the handler's CALLS into SessionQueue, not mutations
///    someone later adds inside an existing family member's body beyond its
///    documented replace, and a future handler call into some new external
///    writer type needs its own pin.
/// 3. ROUTING, NOT PREDICATE: the surface fact pins that the sites call the
///    family, not what the family computes; the membership predicate itself is
///    structurally one loop (the shared TakeUnseen core both halves delegate
///    to) and is additionally pinned behaviorally by
///    SessionQueueAppendUnseenTests.
/// 4. HANDLER-LOCAL: other handlers' queue writes are out of scope (the
///    builder-adjacent ones are covered by DeliveredLaunchStateWriteRosterTests).
/// 5. RESOLUTION SKIPS: an unresolved token behaves differently per fact: on
///    the surface fact it can only drop an actual entry, which fails the
///    equality loudly; on the setter fact it could hide an offender, but the
///    NowPlayingQueue setter is a plain non-generic memberref that always
///    resolves, and no SessionQueue member is generic, so the skip path cannot
///    fire for either fact today.
/// </summary>
public class PlaybackNearlyFinishedQueueWriteRosterTests
{
    /// <summary>
    /// The handler performs ZERO direct NowPlayingQueue assignments: every
    /// session-queue write routes through SessionQueue.AppendUnseen (which owns
    /// the copy + dedup + conditional replace) or the rehydration mirror. There
    /// is deliberately NO allowlist: a site that believes it needs a raw write
    /// is exactly the site this roster exists to surface.
    /// </summary>
    [Fact]
    public void HandlerAssignsNowPlayingQueue_NowhereDirectly()
    {
        Module pluginModule = typeof(BaseHandler).Module;
        var offenders = new SortedSet<string>();

        foreach (MethodBase method in HandlerMethods())
        {
            if (IlCallScanner.CallsNamedMethod(method, pluginModule, "set_NowPlayingQueue", typeof(SessionInfo)))
            {
                offenders.Add(IlCallScanner.LogicalMethodName(method));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"PlaybackNearlyFinishedEventHandler assigns session.NowPlayingQueue directly in [{string.Join(", ", offenders)}]. " +
            "Route the append through SessionQueue.AppendUnseen (copy + membership dedup + conditional replace), or, for a " +
            "whole-queue restore, ProgressReporter.TryRehydrateSessionQueueFromDevice; the derive-then-commit exception list " +
            "lives on the class doc and this roster is its structural guard (JF-712/JF-720).");
    }

    [Fact]
    public void SessionQueueCallSurface_IsExactlyTheDocumentedRoster()
    {
        Module pluginModule = typeof(BaseHandler).Module;

        // (member@caller): the handler's ENTIRE outbound call surface into
        // SessionQueue, reader legs included, compared in BOTH directions. This
        // is the family-sweep completion of the named-target pin: a FUTURE
        // SessionQueue member (the helper's own doc expects the family to grow a
        // lock or merge-style append someday) called from anywhere in the handler
        // appears here undocumented even though no named-target scan could know
        // it. TryFetchContinuationBatch's append and the reader legs are the BY
        // DESIGN pre-build surface; the derive and commit sites are the JF-712
        // policy itself.
        var expected = new SortedSet<string>(StringComparer.Ordinal)
        {
            "IndexOfQueueItem@FindCurrentQueueIndex",
            "IndexOfQueueItem@CachedNextStillFollowsCurrent",
            "IdSet@TryAutoAdvanceNextEpisodeAsync",
            "AppendUnseen@TryFetchContinuationBatch",
            "AppendUnseen@CommitPendingContinuation",
            "UnseenItems@DeriveSimilarTracksPopulation",
        };

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (MethodBase method in HandlerMethods())
        {
            foreach (int token in IlCallScanner.CallTokens(method))
            {
                if (IlCallScanner.TryResolveMethod(pluginModule, token) is { } callee
                    && callee.DeclaringType == typeof(SessionQueue))
                {
                    actual.Add($"{callee.Name}@{IlCallScanner.LogicalMethodName(method)}");
                }
            }
        }

        Assert.True(
            actual.SetEquals(expected),
            "PlaybackNearlyFinishedEventHandler's SessionQueue call surface drifted from the documented roster: " +
            $"missing [{string.Join(", ", expected.Except(actual))}], undocumented [{string.Join(", ", actual.Except(expected))}]. " +
            "A new site needs its BY DESIGN marker on the class doc and an entry here (or this roster has drifted and " +
            "needs updating with the reason); a queue WRITE also belongs in SessionQueue.AppendUnseen, never inline.");
    }

    /// <summary>
    /// The one queue-affecting call the handler makes OUTSIDE the SessionQueue
    /// family: the JF-574 entry rehydration mirror, whose own write lives inside
    /// ProgressReporter. A whole ProgressReporter member sweep would be noise
    /// (the handler legitimately calls many of its members), so this leg stays a
    /// named-target pin: exactly HandleAsync, the method that owns the entry.
    /// </summary>
    [Fact]
    public void RehydrationMirror_IsCalledOnlyFromHandleAsync()
    {
        HashSet<int> tokens = new(IlCallScanner.MethodTokens(
            typeof(ProgressReporter), nameof(ProgressReporter.TryRehydrateSessionQueueFromDevice)));

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (MethodBase method in HandlerMethods())
        {
            if (IlCallScanner.ContainsCallToAnyToken(method, tokens))
            {
                actual.Add(IlCallScanner.LogicalMethodName(method));
            }
        }

        Assert.True(
            actual.SetEquals(new[] { "HandleAsync" }),
            $"ProgressReporter.TryRehydrateSessionQueueFromDevice must be called only from PlaybackNearlyFinishedEventHandler.HandleAsync " +
            $"(the entry rehydration mirror); found callers [{string.Join(", ", actual)}].");
    }

    /// <summary>
    /// Every method of the handler under test, its compiler-generated closure
    /// included (async state machines, lambdas, local functions), stopping at
    /// the shared BaseHandler (whose own members are not this handler's sites).
    /// </summary>
    private static IEnumerable<MethodBase> HandlerMethods()
        => IlCallScanner.HandlerChainMethods(typeof(PlaybackNearlyFinishedEventHandler), typeof(BaseHandler));
}
