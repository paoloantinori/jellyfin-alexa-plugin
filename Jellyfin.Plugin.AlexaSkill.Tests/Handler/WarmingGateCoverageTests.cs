using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-465: the Layer-1 warming-gate roster as a TEST instead of prose enumerations
/// (the CLAUDE.md Layer-1 list and the SkillWarmingUpTests comment had both gone
/// stale when a gated handler was added). Ground truth is discovered by scanning
/// the plugin assembly's IL method bodies for call/callvirt to a gate entry
/// (BaseHandler.GuardIndexReady or a direct IndexWarmingGate.EnsureReady bypass)
/// inside every non-abstract BaseHandler subclass, then compared against the
/// expected roster below in BOTH directions. Adding a gated handler without
/// updating <see cref="ExpectedGatedHandlers"/> fails here with the new types
/// listed; removing a gate from a listed handler fails the other way. The scan
/// uses only MethodBase.GetMethodBody IL bytes and MetadataToken resolution (the
/// gate methods are methoddefs in the same assembly), so no IL disassembler
/// dependency is needed. Layer-2 choke points (ArtistSearch, SongNgramIndexService)
/// are deliberately NOT BaseHandler subclasses and never appear here.
/// </summary>
public class WarmingGateCoverageTests
{
    /// <summary>
    /// Every handler whose request path would hit the cold database and therefore
    /// gates at entry. When this roster changes, also decide whether the new gate
    /// needs a Layer-1 reachability test in SkillWarmingUpTests and whether the
    /// CLAUDE.md Layer-1 note still describes the routing correctly (it intentionally
    /// points HERE as the source of truth instead of enumerating handlers).
    /// </summary>
    private static readonly HashSet<Type> ExpectedGatedHandlers = new()
    {
        typeof(AddToQueueIntentHandler),
        typeof(FindSongIntentHandler),
        typeof(PlayAlbumIntentHandler),
        typeof(PlayArtistSongsIntentHandler),
        typeof(PlayByGenreIntentHandler),
        typeof(PlayMoodMusicIntentHandler),
        typeof(PlayNextIntentHandler),
        typeof(PlaySongIntentHandler),
        typeof(QueryArtistLibraryIntentHandler),
        typeof(SearchMediaIntentHandler)
    };

    [Fact]
    public void GatedHandlerRoster_MatchesAssemblyScan()
    {
        HashSet<Type> discovered = ScanGatedHandlers();

        var listedButNotGating = new SortedSet<string>(ExpectedGatedHandlers.Except(discovered).Select(t => t.Name));
        var gatingButNotListed = new SortedSet<string>(discovered.Except(ExpectedGatedHandlers).Select(t => t.Name));

        Assert.True(
            listedButNotGating.Count == 0 && gatingButNotListed.Count == 0,
            "Warming-gate roster drifted from the assembly scan. " +
            (listedButNotGating.Count > 0
                ? $"Listed but no longer gating: [{string.Join(", ", listedButNotGating)}]. "
                : string.Empty) +
            (gatingButNotListed.Count > 0
                ? $"Gating but NOT in ExpectedGatedHandlers (add there, and consider a SkillWarmingUpTests reachability test): [{string.Join(", ", gatingButNotListed)}]."
                : string.Empty));
    }

    /// <summary>
    /// Every non-abstract BaseHandler subclass (directly or via an intermediate
    /// base) with at least one gate call in ANY of its methods, including private
    /// helpers and lambdas/local functions compiled to nested types.
    /// </summary>
    private static HashSet<Type> ScanGatedHandlers()
    {
        Assembly pluginAssembly = typeof(BaseHandler).Assembly;

        // Open-world over the gate SURFACE (JF-465 review): enumerate every
        // GuardIndexReady/EnsureReady overload by name instead of hardcoding the
        // current two-plus-two, so a future overload (a third index type) cannot
        // silently escape the scan and leave a stale roster behind a green suite.
        HashSet<int> gateTokens = new();
        const BindingFlags allDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (MethodInfo m in typeof(BaseHandler).GetMethods(allDeclared).Where(m => m.Name == "GuardIndexReady"))
        {
            gateTokens.Add(m.MetadataToken);
        }

        foreach (MethodInfo m in typeof(IndexWarmingGate).GetMethods(allDeclared).Where(m => m.Name == nameof(IndexWarmingGate.EnsureReady)))
        {
            gateTokens.Add(m.MetadataToken);
        }

        var gated = new HashSet<Type>();
        foreach (Type handlerType in pluginAssembly.GetTypes())
        {
            if (!typeof(BaseHandler).IsAssignableFrom(handlerType) || handlerType == typeof(BaseHandler) || handlerType.IsAbstract)
            {
                continue;
            }

            // Walk the base chain up to (excluding) BaseHandler: a gate call in an
            // intermediate base class belongs to every concrete handler under it
            // (JF-465 review: DeclaredOnly on the handler alone missed that shape).
            for (Type? chainType = handlerType; chainType != null && chainType != typeof(BaseHandler); chainType = chainType.BaseType)
            {
                foreach (Type type in NestedTypeClosure(chainType!))
                {
                    IEnumerable<MethodBase> callables = type.GetMethods(allDeclared | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                        .Concat(type.GetConstructors(allDeclared | BindingFlags.DeclaredOnly));
                    foreach (MethodBase method in callables)
                    {
                        if (ContainsGateCall(method, gateTokens))
                        {
                            gated.Add(handlerType);
                            break;
                        }
                    }
                }
            }
        }

        return gated;
    }

    private static IEnumerable<Type> NestedTypeClosure(Type root)
    {
        var queue = new Queue<Type>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            Type current = queue.Dequeue();
            yield return current;
            foreach (Type nested in current.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                queue.Enqueue(nested);
            }
        }
    }

    /// <summary>
    /// Looks for the call (0x28) or callvirt (0x6F) opcode directly followed by a
    /// metadata token resolving to a gate entry. The operand window is checked at
    /// every offset, so a coincidental match inside another instruction's operand
    /// could only ADD a type to the discovered set, which fails the roster equality
    /// loudly; it can never silently hide a real gated handler.
    /// </summary>
    private static bool ContainsGateCall(MethodBase method, HashSet<int> gateTokens)
    {
        MethodBody? body = method.GetMethodBody();
        if (body == null)
        {
            return false;
        }

        byte[] il = body.GetILAsByteArray() ?? Array.Empty<byte>();
        for (int i = 0; i + 5 <= il.Length; i++)
        {
            if ((il[i] == 0x28 || il[i] == 0x6F) && gateTokens.Contains(BitConverter.ToInt32(il, i + 1)))
            {
                return true;
            }
        }

        return false;
    }
}
