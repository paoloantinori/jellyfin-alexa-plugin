#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-674: the CONSTRUCTION-site roster for the queue-continuation identity. The
/// fetch-time validation (<see cref="QueueContinuation.IsForLiveQueue"/>, called by
/// <see cref="PlaybackNearlyFinishedEventHandler.TryFetchContinuationBatch"/>) is
/// only as good as the identity the mint sites capture: an entry minted WITHOUT
/// <see cref="QueueContinuation.MintedQueueItemIds"/> skips validation entirely
/// (the documented hand-constructed-test carve-out), so a future sixth mint site
/// that forgets the initializer silently reopens the stale-continuation injection
/// this task closes. This roster makes the miss structural in the
/// PlaybackNearlyFinishedQueueWriteRoster idiom (JF-720): every method in the
/// plugin assembly that constructs a <see cref="QueueContinuation"/> must also
/// initialize the identity property in the same method body (an init-only
/// property is only assignable from an object initializer or immediately after
/// construction, and both compile to the setter call scanned here).
/// The five construction sites today: PlayArtistSongsIntentHandler and
/// CrossMediaFallback (the Artist arms), AlbumPlayService (the Album and Playlist
/// arms), and PlayBookIntentHandler's ApplyBookPlaybackState local function
/// (reached through the assembly walk: local functions compile to nested types).
/// SELF-RED: deleting the initializer from any one site flips the roster.
/// ACCEPTED BOUNDARIES (the roster idiom's documented-limit class):
/// 1. SAME-METHOD SCOPE: the scan ties the setter call to the constructing
///    method. A site that constructs the entry in one method and hands it to a
///    shared helper that sets the property would pass vacuously (the helper sets
///    it, the constructor site does not); no such split shape exists (init-only
///    properties make it awkward on purpose), and a future one needs its own pin.
/// 2. DERIVED TYPES: ConstructsType uses IsAssignableFrom, so a hypothetical
///    QueueContinuation subclass constructs are covered; none exists today.
/// 3. TEST ASSEMBLY: hand-constructed continuations in the TEST assembly are the
///    documented carve-out (empty identity skips validation) and are deliberately
///    outside this plugin-assembly scan.
/// </summary>
public class QueueContinuationIdentityRosterTests
{
    /// <summary>
    /// Every plugin-assembly method that news up a QueueContinuation initializes
    /// MintedQueueItemIds: the identity capture cannot be forgotten at a new mint
    /// site without failing this roster.
    /// </summary>
    [Fact]
    public void EveryQueueContinuationConstructionSite_WiresMintedQueueItemIds()
    {
        Module pluginModule = typeof(BaseHandler).Module;
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        int constructionSites = 0;

        foreach ((Type _, MethodBase method) in IlCallScanner.DeclaredMethods(pluginModule.Assembly))
        {
            if (!IlCallScanner.ConstructsType(method, pluginModule, typeof(QueueContinuation)))
            {
                continue;
            }

            constructionSites++;
            if (!IlCallScanner.CallsNamedMethod(method, pluginModule, "set_MintedQueueItemIds", typeof(QueueContinuation)))
            {
                offenders.Add(IlCallScanner.LogicalMethodName(method));
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"QueueContinuation is constructed without MintedQueueItemIds in [{string.Join(", ", offenders)}]. " +
            "Every mint site must capture the queue page it installs (QueueContinuation.QueueIdsOf(queueItems)) " +
            "or the JF-674 fetch-time identity validation silently skips the entry (JF-674).");
        Assert.True(
            constructionSites > 0,
            $"The scan found ZERO QueueContinuation construction sites (was {constructionSites}); " +
            "the roster went vacuous: the construction scan no longer sees the sites it guards. " +
            $"Today's expected site count is five (the two Artist arms, the Album and Playlist arms, PlayBook's ApplyBookPlaybackState); a different count with no offenders is a mint-site consolidation to review, not a scan failure.");
    }

    /// <summary>
    /// The fetch guard actually validates: TryFetchContinuationBatch (its async
    /// state machine included, attributed back through LogicalMethodName) calls
    /// IsForLiveQueue. Without this pin, the identity could be captured everywhere
    /// and still never consulted (the validation call is the other half of the
    /// design).
    /// </summary>
    [Fact]
    public void TryFetchContinuationBatch_CallsTheIdentityValidation()
    {
        Module pluginModule = typeof(BaseHandler).Module;
        IEnumerable<MethodBase> fetchMethods = IlCallScanner
            .HandlerChainMethods(typeof(PlaybackNearlyFinishedEventHandler), typeof(BaseHandler))
            .Where(m => string.Equals(IlCallScanner.LogicalMethodName(m), "TryFetchContinuationBatch", StringComparison.Ordinal));

        Assert.True(
            fetchMethods.Any(m => IlCallScanner.CallsNamedMethod(m, pluginModule, "IsForLiveQueue", typeof(QueueContinuation))),
            "PlaybackNearlyFinishedEventHandler.TryFetchContinuationBatch no longer calls QueueContinuation.IsForLiveQueue; " +
            "the JF-674 queue-identity validation was removed or moved away from the fetch path.");
    }

    /// <summary>
    /// The identity design's resume-path assumption, made structural: the handlers
    /// that ReplaceAll-relaunch the SAME logical queue (the design decision's
    /// verified list) never assign <c>session.NowPlayingQueue</c>, which is exactly
    /// why a minted page stays a member of the live queue across a stop-and-resume
    /// and the continuation keeps serving (the JF-574 linger semantics). A future
    /// "cleanup" that installs a fresh one-item queue on any of these paths (the
    /// AttachNowPlayingIfLaunched single-item form is the natural shape) silently
    /// makes every minted page fail IsForLiveQueue, so multi-page sources stop
    /// advancing after a resume with no other test failing; this setter scan is
    /// what reds (the JF-720 NowPlayingQueue-setter idiom, scoped to the resume
    /// family). YesIntentHandler is deliberately NOT in the set: its confirm legs
    /// legitimately rebuild the queue (a NEW logical queue whose full list supersets
    /// the minted page, so the identity still validates), and its resume-confirm
    /// arm shares the handler type with them.
    /// </summary>
    [Fact]
    public void ResumePathHandlers_NeverAssignNowPlayingQueueDirectly()
    {
        Module pluginModule = typeof(BaseHandler).Module;
        Type[] resumeHandlers =
        {
            typeof(ResumeIntentHandler),
            typeof(LaunchRequestHandler),
            typeof(StartOverIntentHandler),
            typeof(JumpToPositionIntentHandler),
            typeof(SkipForwardBackIntentHandler),
        };

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Type handler in resumeHandlers)
        {
            foreach (MethodBase method in IlCallScanner.HandlerChainMethods(handler, typeof(BaseHandler)))
            {
                if (IlCallScanner.CallsNamedMethod(method, pluginModule, "set_NowPlayingQueue", typeof(SessionInfo)))
                {
                    offenders.Add($"{handler.Name}.{IlCallScanner.LogicalMethodName(method)}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"A resume-path handler assigns session.NowPlayingQueue directly in [{string.Join(", ", offenders)}]. " +
            "The resume paths must relaunch WITHOUT touching the queue: a queue replacement there breaks the JF-674 " +
            "identity validation (the minted page leaves the live queue) and multi-page sources stop advancing after " +
            "a resume; install the queue only on the play/confirm paths that mint it.");
    }
}
