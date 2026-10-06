using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-785 Leg A enforcement (the code-review round's finding: the belt
/// invariant had no machine-checkable form): every family that guards its
/// stateful write with <see cref="PlaybackLaunchBuilder.HasCurrentPlaybackEvidence"/>
/// must ALSO pass <c>allowLedgerTailAnswers: false</c> at its
/// <see cref="PlaybackLaunchBuilder.ResolveCurrentPlayingItem"/> call. A future
/// handler that copies the documented guard alone and calls the resolver with
/// the default reopens the unresolvable-evidence door (the days-old ledger tail
/// taking the stateful write), exactly the JF-627 gate-marker finding-1 class,
/// and compiles clean while doing it. Ground truth is discovered by scanning
/// the plugin assembly's IL (the WarmingGateCoverageTests precedent, over the
/// shared <see cref="IlCallScanner"/>): a resolver call is classified FLAGGED
/// when the instruction immediately before the call (nops skipped) is
/// <c>ldc.i4.0</c>, the literal false the guarded sites pass as the flag (the
/// last argument is pushed last). A future site passing a bool VARIABLE would
/// classify unflagged and fail the rosters loudly here, never silently.
/// </summary>
public class GuardedResolverTailRosterTests
{
    /// <summary>
    /// The families deliberately riding the resolver's unbounded tail with the
    /// default flag: RateItem's JF-626 stance plus the two unguarded transport
    /// riders. A new unguarded family is a deliberate decision: add it here and
    /// the failure message tells the next reader which roster moved.
    /// </summary>
    private static readonly HashSet<string> ExpectedUnguardedCallers = new()
    {
        typeof(RateItemIntentHandler).Name,
        typeof(RepeatIntentHandler).Name,
        typeof(SetPlaybackSpeedIntentHandler).Name,
    };

    [Fact]
    public void EvidenceGuardCallers_RefuseTheLedgerTail()
    {
        foreach (string guardCaller in GuardCallerTypes())
        {
            Assert.True(
                FlaggedCallerTypes().Contains(guardCaller),
                $"{guardCaller} calls HasCurrentPlaybackEvidence but none of its ResolveCurrentPlayingItem call sites " +
                "pass allowLedgerTailAnswers: false (the JF-785 Leg A belt; the guard alone leaves the unresolvable-evidence door open).");
        }
    }

    [Fact]
    public void UnflaggedResolverCallers_MatchExpectedRoster()
    {
        SortedSet<string> unflagged = UnflaggedCallerTypes();
        var listedButNotCalling = new SortedSet<string>(ExpectedUnguardedCallers.Except(unflagged));
        var callingButNotListed = new SortedSet<string>(unflagged.Except(ExpectedUnguardedCallers));

        Assert.True(
            listedButNotCalling.Count == 0 && callingButNotListed.Count == 0,
            "The unguarded (default-flag) resolver-caller roster drifted from the assembly scan. " +
            (listedButNotCalling.Count > 0
                ? $"Listed but no longer calling unflagged: [{string.Join(", ", listedButNotCalling)}]. "
                : string.Empty) +
            (callingButNotListed.Count > 0
                ? $"Calling unflagged but NOT in ExpectedUnguardedCallers (either pass allowLedgerTailAnswers: false with the evidence guard, or record the deliberate unguarded stance here): [{string.Join(", ", callingButNotListed)}]."
                : string.Empty));
    }

    /// <summary>The top-level types whose IL calls the evidence predicate.</summary>
    private static HashSet<string> GuardCallerTypes()
        => TypesCalling(IlCallScanner.MethodTokens(
            typeof(PlaybackLaunchBuilder), nameof(PlaybackLaunchBuilder.HasCurrentPlaybackEvidence)).ToList());

    /// <summary>The top-level types with at least one FLAGGED resolver call site.</summary>
    private static HashSet<string> FlaggedCallerTypes()
    {
        IReadOnlyCollection<int> resolverTokens = IlCallScanner.MethodTokens(
            typeof(PlaybackLaunchBuilder), nameof(PlaybackLaunchBuilder.ResolveCurrentPlayingItem)).ToList();
        return ResolverCallSites(resolverTokens)
            .Where(site => site.Flagged)
            .Select(site => site.CallerType)
            .ToHashSet();
    }

    /// <summary>The top-level types with at least one UNFLAGGED resolver call site.</summary>
    private static SortedSet<string> UnflaggedCallerTypes()
    {
        IReadOnlyCollection<int> resolverTokens = IlCallScanner.MethodTokens(
            typeof(PlaybackLaunchBuilder), nameof(PlaybackLaunchBuilder.ResolveCurrentPlayingItem)).ToList();
        return new SortedSet<string>(ResolverCallSites(resolverTokens)
            .Where(site => !site.Flagged)
            .Select(site => site.CallerType));
    }

    /// <summary>The top-level types whose IL calls any of the given tokens (lambdas and
    /// state machines are attributed to their owning type through the nested-type walk).</summary>
    private static HashSet<string> TypesCalling(IReadOnlyCollection<int> tokens)
        => IlCallScanner.DeclaredMethods(typeof(BaseHandler).Assembly)
            .Where(kv => IlCallScanner.ContainsCallToAnyToken(kv.Method, tokens))
            .Select(kv => IlCallScanner.TopLevelType(kv.Type).Name)
            .ToHashSet();

    /// <summary>
    /// Every resolver call site in the plugin assembly as (top-level caller type,
    /// flagged): the flagged classification looks at the instruction immediately
    /// preceding the call (nops skipped), the push of the LAST argument, and
    /// answers flagged exactly when it is <c>ldc.i4.0</c> (0x16), the literal false.
    /// </summary>
    private static IEnumerable<(string CallerType, bool Flagged)> ResolverCallSites(IReadOnlyCollection<int> resolverTokens)
    {
        foreach ((Type type, MethodBase method) in IlCallScanner.DeclaredMethods(typeof(BaseHandler).Assembly))
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
            {
                continue;
            }

            var instructions = IlCallScanner.Instructions(il).ToList();
            for (int i = 0; i < instructions.Count; i++)
            {
                (int offset, short opcode, int operandStart, int _) = instructions[i];
                bool isCall = opcode is 0x28 or 0x6F;
                if (!isCall || !resolverTokens.Contains(BitConverter.ToInt32(il, operandStart)))
                {
                    continue;
                }

                // Walk back over nops to the last argument push.
                int previous = i - 1;
                while (previous >= 0 && instructions[previous].Opcode == 0x00)
                {
                    previous--;
                }

                yield return (
                    IlCallScanner.TopLevelType(type).Name,
                    previous >= 0 && instructions[previous].Opcode == 0x16);
            }
        }
    }
}
