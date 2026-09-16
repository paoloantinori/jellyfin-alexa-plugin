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
/// their top-level declaring type.
/// </summary>
public class SessionQueueReaderRosterTests
{
    /// <summary>
    /// Readers that adopted the shared guard: each calls
    /// <see cref="ProgressReporter.TryRehydrateSessionQueueFromDevice"/> before its
    /// queue read (asserted mechanically below, so the ADOPTED label cannot rot).
    /// </summary>
    private static readonly HashSet<Type> AdoptedReaders = new()
    {
        typeof(NextIntentHandler),
        typeof(PreviousIntentHandler),
        typeof(ListQueueIntentHandler),
        typeof(PlaybackNearlyFinishedEventHandler),
        typeof(AplUserEventHandler)
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
    ///   coherence legs and the rebuild.
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
            foreach (MethodBase method in DeclaredCallableMethods(type))
            {
                if (CallsGetter(method, pluginModule, getter))
                {
                    readers.Add(TopLevelType(method.DeclaringType ?? type));
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
            foreach (MethodBase method in DeclaredCallableMethods(type))
            {
                if (ContainsCallToToken(method, targetToken))
                {
                    callers.Add(TopLevelType(method.DeclaringType ?? type));
                    break;
                }
            }
        }

        return callers;
    }

    private static IEnumerable<MethodBase> DeclaredCallableMethods(Type type)
    {
        const BindingFlags allDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        return type.GetMethods(allDeclared | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(type.GetConstructors(allDeclared | BindingFlags.DeclaredOnly));
    }

    private static bool CallsGetter(MethodBase method, Module module, MethodInfo getter)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            return false;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F)
            {
                continue;
            }

            int token = BitConverter.ToInt32(il, i + 1);

            // Only MethodDef (0x06) and MemberRef (0x0A) tokens can name the getter;
            // resolving anything else (e.g. a MethodSpec) throws, and a failed
            // resolution can only SKIP a candidate, which fails the roster equality
            // loudly the moment that candidate is the only reader of a new consumer.
            int table = unchecked((int)((uint)token >> 24));
            if (table != 0x06 && table != 0x0A)
            {
                continue;
            }

            MemberInfo? resolved;
            try
            {
                resolved = module.ResolveMethod(token, null, null);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (resolved is MethodInfo m
                && m.Name == getter.Name
                && m.DeclaringType == getter.DeclaringType)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsCallToToken(MethodBase method, int targetToken)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            return false;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i + 5 <= il.Length; i++)
        {
            if ((il[i] == 0x28 || il[i] == 0x6F) && BitConverter.ToInt32(il, i + 1) == targetToken)
            {
                return true;
            }
        }

        return false;
    }

    private static Type TopLevelType(Type type)
    {
        Type current = type;
        while (current.IsNested && current.DeclaringType != null)
        {
            current = current.DeclaringType;
        }

        return current;
    }
}
