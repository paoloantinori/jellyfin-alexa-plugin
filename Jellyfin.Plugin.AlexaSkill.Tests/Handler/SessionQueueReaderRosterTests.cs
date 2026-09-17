using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using MediaBrowser.Controller.Session;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-579: the session-queue reader roster as a TEST instead of prose (the
/// WarmingGateCoverageTests precedent). Ground truth is discovered by scanning the
/// plugin assembly's IL method bodies for call/callvirt to the
/// <see cref="SessionInfo.NowPlayingQueue"/> property getter, then compared against
/// the roster below in BOTH directions. A new consumer that reads the session queue
/// without adopting the JF-577 rehydration guard (or without being listed here as a
/// deliberate exemption) fails this test by name, so the next wiped-queue consumer
/// cannot be forgotten silently (QueueRehydrationAdoptionTests is behavioral and only
/// pins the consumers someone remembered). The scan resolves each call token through
/// <see cref="Module.ResolveMethod(int,Type[],Type[])"/> (the getter is a memberref
/// into MediaBrowser.Controller, unlike the same-assembly methoddef the warming-gate
/// scan compares raw), mapping nested types (async state machines, closures) up to
/// their top-level declaring type. The IL walking lives in the shared
/// <see cref="IlCallScanner"/> (JF-582, with WarmingGateCoverageTests).
/// </summary>
public class SessionQueueReaderRosterTests
{
    /// <summary>
    /// Readers that adopted the shared guard: each calls
    /// <see cref="ProgressReporter.TryRehydrateSessionQueueFromDevice"/> before its
    /// queue read (asserted mechanically below, so the ADOPTED label cannot rot).
    /// The next/previous entry points (NextIntentHandler,
    /// PreviousIntentHandler, AplUserEventHandler taps) no longer read the queue
    /// directly: JF-582 moved their bodies into the shared
    /// ProgressReporter.ServeAdjacentQueueItem, which calls the guard itself (the
    /// owner exemption below covers it; QueueRehydrationAdoptionTests pins their
    /// rehydration behavior).
    /// </summary>
    private static readonly HashSet<Type> AdoptedReaders = new()
    {
        typeof(ListQueueIntentHandler),
        typeof(PlaybackNearlyFinishedEventHandler)
    };

    /// <summary>
    /// Deliberate non-adopters, each with its reason:
    /// - PlaybackStartedEventHandler: precompute cache-write only; the JF-577 skip
    ///   (NearlyFinished's own guard owns the user-visible resolution one event later).
    /// - AddToQueueIntentHandler: the JF-578 skip; it writes only the session queue,
    ///   so adoption waits for the shared both-stores membership writer.
    /// - PlayNextIntentHandler: same writer shape as AddToQueue (InsertAfterCurrent
    ///   composes the session queue only); JF-578 family.
    /// - LaunchRequestHandler: the legacy session-queue resume reads it for its own
    ///   resume offer (the device last-played ledger offer comes first).
    /// - PlayIntentHandler: the resume fallback (queue head); same legacy resume
    ///   family as LaunchRequest.
    /// - ClearQueueIntentHandler: logging-only count read; its purpose is to wipe
    ///   both queue stores, so rehydrating first would undo the user's ask.
    /// - ProgressReporter: the shared guard/mirror itself; its reads ARE the
    ///   coherence legs and the rebuild, and since JF-582 also the shared
    ///   adjacent-queue-item serve the four next/previous entry points route
    ///   through (it calls the guard from inside, as the owner).
    /// - SessionQueue: passive index/id-set scan helper; its only callers are
    ///   PlaybackStarted (exempt) and PlaybackNearlyFinished (adopted), so adoption
    ///   is enforced at the callers.
    /// </summary>
    private static readonly HashSet<Type> ExemptReaders = new()
    {
        typeof(PlaybackStartedEventHandler),
        typeof(AddToQueueIntentHandler),
        typeof(PlayNextIntentHandler),
        typeof(LaunchRequestHandler),
        typeof(PlayIntentHandler),
        typeof(ClearQueueIntentHandler),
        typeof(ProgressReporter),
        typeof(SessionQueue)
    };

    [Fact]
    public void SessionQueueReaderRoster_MatchesAssemblyScan()
    {
        HashSet<Type> discovered = ScanPropertyReaders(
            typeof(SessionInfo).GetProperty(nameof(SessionInfo.NowPlayingQueue)) ?? throw new InvalidOperationException("NowPlayingQueue property not found"));

        var expected = AdoptedReaders.Concat(ExemptReaders).ToHashSet();
        var listedButNotReading = new SortedSet<string>(expected.Except(discovered).Select(t => t.Name));
        var readingButNotListed = new SortedSet<string>(discovered.Except(expected).Select(t => t.Name));

        Assert.True(
            listedButNotReading.Count == 0 && readingButNotListed.Count == 0,
            "Session-queue reader roster drifted from the assembly scan. " +
            (listedButNotReading.Count > 0
                ? $"Listed but no longer reading session.NowPlayingQueue: [{string.Join(", ", listedButNotReading)}]. "
                : string.Empty) +
            (readingButNotListed.Count > 0
                ? $"Reads session.NowPlayingQueue but is NOT in the roster: [{string.Join(", ", readingButNotListed)}]. Either adopt ProgressReporter.TryRehydrateSessionQueueFromDevice before the queue read (and add a QueueRehydrationAdoptionTests pin) or add the type to ExemptReaders with a reason."
                : string.Empty));
    }

    [Fact]
    public void AdoptedReaders_CallTheSharedRehydrationGuard()
    {
        HashSet<Type> guardCallers = ScanSameAssemblyCallers(
            typeof(ProgressReporter).GetMethod(
                nameof(ProgressReporter.TryRehydrateSessionQueueFromDevice),
                BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("TryRehydrateSessionQueueFromDevice not found"));

        // The guard OWNER also calls it since JF-582, but ONLY from the two
        // members that own the serve (the combined rehydrate-and-resolve helper and
        // the shared adjacent serve). Strip exactly those two methods' calls, not
        // the whole type (review finding, JF-582): a FUTURE ProgressReporter method
        // that reads the queue and skips the guard must still fail this proof, and a
        // type-wide removal would silently exempt it.
        // The combined helper is PRIVATE by design (the JF-582 /simplify pass: a
        // public wrapper would be a door that skips the JF-564/JF-507 gates), so it
        // is named by string; the existence assertion below fails loudly on a rename.
        const string ownerMethodName = "TryRehydrateAndResolveCurrentItemId";
        const string serveMethodName = nameof(ProgressReporter.ServeAdjacentQueueItem);
        int targetToken = typeof(ProgressReporter).GetMethod(
            nameof(ProgressReporter.TryRehydrateSessionQueueFromDevice),
            BindingFlags.Public | BindingFlags.Static)!.MetadataToken;
        bool ownerStillCalls = false;
        foreach (MethodBase m in IlCallScanner.DeclaredCallableMethods(typeof(ProgressReporter)))
        {
            if (m.Name != ownerMethodName && m.Name != serveMethodName)
            {
                continue;
            }

            if (IlCallScanner.ContainsCallToToken(m, targetToken))
            {
                ownerStillCalls = true;
            }
        }

        MethodInfo? ownerMethod = typeof(ProgressReporter).GetMethod(
            ownerMethodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(ownerMethod != null, $"ProgressReporter.{ownerMethodName} was renamed or made public; update this exemption.");
        Assert.True(ownerStillCalls, "The serve owner no longer calls the guard internally; update this exemption.");
        guardCallers.Remove(typeof(ProgressReporter));

        var claimedButNotCalling = new SortedSet<string>(AdoptedReaders.Except(guardCallers).Select(t => t.Name));
        var callingButNotClaimed = new SortedSet<string>(guardCallers.Except(AdoptedReaders).Select(t => t.Name));

        Assert.True(
            claimedButNotCalling.Count == 0 && callingButNotClaimed.Count == 0,
            "ADOPTED roster is not backed by real guard calls. " +
            (claimedButNotCalling.Count > 0
                ? $"Listed as adopted but never calls the guard: [{string.Join(", ", claimedButNotCalling)}]. "
                : string.Empty) +
            (callingButNotClaimed.Count > 0
                ? $"Calls the guard but is not in AdoptedReaders: [{string.Join(", ", callingButNotClaimed)}]."
                : string.Empty));
    }

    /// <summary>
    /// Every type whose methods (anywhere in the nested-type closure, including async
    /// state machines and closures) call the getter of the given property. Nested
    /// types are attributed to their top-level declaring type.
    /// </summary>
    private static HashSet<Type> ScanPropertyReaders(PropertyInfo property)
    {
        Module pluginModule = typeof(BaseHandler).Module;
        MethodInfo getter = property.GetMethod ?? throw new InvalidOperationException("Property has no getter");
        var readers = new HashSet<Type>();

        foreach (Type type in typeof(BaseHandler).Assembly.GetTypes())
        {
            foreach (MethodBase method in IlCallScanner.DeclaredCallableMethods(type))
            {
                if (IlCallScanner.CallsGetter(method, pluginModule, getter))
                {
                    readers.Add(IlCallScanner.TopLevelType(method.DeclaringType ?? type));
                    break;
                }
            }
        }

        return readers;
    }

    /// <summary>
    /// Every top-level type whose method bodies contain a call/callvirt whose raw IL
    /// operand equals the metadata token of the given same-assembly method (the
    /// WarmingGateCoverageTests technique: a methoddef token is compared verbatim, no
    /// resolution needed).
    /// </summary>
    private static HashSet<Type> ScanSameAssemblyCallers(MethodInfo target)
    {
        int targetToken = target.MetadataToken;
        var callers = new HashSet<Type>();

        foreach (Type type in typeof(BaseHandler).Assembly.GetTypes())
        {
            foreach (MethodBase method in IlCallScanner.DeclaredCallableMethods(type))
            {
                if (IlCallScanner.ContainsCallToToken(method, targetToken))
                {
                    callers.Add(IlCallScanner.TopLevelType(method.DeclaringType ?? type));
                    break;
                }
            }
        }

        return callers;
    }
}
