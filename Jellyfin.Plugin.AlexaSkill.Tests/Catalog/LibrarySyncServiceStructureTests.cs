#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-706 structural pins for the catalog-sync per-type leg wiring. Standalone
/// collection-free IL-scan class (no plugin fixture needed), following the
/// WarmingGateCoverageTests / CatalogValueFactoryTests discipline: shared
/// IlCallScanner, loud-only failure. Every catalog type must flow through the
/// ONE SyncTypeLegAsync call site (the loop over the per-type wiring table in
/// RunLegAsync, which the injection gate and the UpdateInteractionModelAsync
/// extraction derive from) and onward through the ONE SyncCatalogForLocaleAsync
/// call site inside it, so the JF-695 freeze-isolation try, the
/// TypeLegEntryProbeForTest seam, and the minted/id-forwarding bookkeeping
/// cannot be bypassed for any type.
/// </summary>
public class LibrarySyncServiceStructureTests
{
    /// <summary>
    /// The per-type loop is the single SyncTypeLegAsync call site. Roslyn
    /// mangles local functions to names like
    /// "&lt;SyncUserLibraryAsync&gt;g__SyncTypeLegAsync|3" (the g__ marker and
    /// ordinal suffix on a compiler-generated display class; the async body
    /// lives on the state machine's MoveNext), so the scan matches the marker,
    /// not the bare name. Call INSTRUCTIONS are counted, not calling methods:
    /// the three pre-JF-706 call sites all sat inside one method (RunLegAsync's
    /// state machine MoveNext), so a method-level count reads 1 either way and
    /// pins nothing. Before JF-706 the three parallel Artist/Album/Series call
    /// sites made a fourth catalog type a three-site edit plus the gate and six
    /// positional injection arguments (the repo's "missed one" bug class: a
    /// missed site compiles clean and freezes or syncs inconsistently);
    /// re-expanding them fails here first with the offender named.
    /// </summary>
    [Fact]
    public void SyncTypeLegAsync_HasExactlyOneCallSite_TheTypeLoop()
    {
        var assembly = typeof(LibrarySyncService).Assembly;

        List<MethodBase> stubs = IlCallScanner.DeclaredMethods(assembly)
            .Where(pair => pair.Method.Name.Contains("g__SyncTypeLegAsync|", StringComparison.Ordinal))
            .Select(pair => pair.Method)
            .ToList();
        MethodBase stub = Assert.Single(stubs);

        AssertSingleCallSite(assembly, stub.MetadataToken, "SyncTypeLegAsync", "the JF-706 loop over the per-type wiring table");
    }

    /// <summary>
    /// SyncCatalogForLocaleAsync is reachable ONLY via a DIRECT call through the
    /// type-leg stub (the scan counts call/callvirt instructions on the methoddef
    /// token; a method-group delegate or reflection invocation would add a logical
    /// call site this pin cannot see - the JF-706 gate-marker's named boundary):
    /// a future direct call to it (private, same class, exact-purpose
    /// signature) would bypass the JF-695 CatalogPayloadInvariantException
    /// isolation try, the probe seam, and the minted/id-forwarding rule for
    /// that path while the call-site pin above stays green.
    /// </summary>
    [Fact]
    public void SyncCatalogForLocaleAsync_IsCalledOnlyByTheTypeLegStub()
    {
        var assembly = typeof(LibrarySyncService).Assembly;

        MethodBase? worker = typeof(LibrarySyncService).GetMethod(
            "SyncCatalogForLocaleAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(worker);

        AssertSingleCallSite(assembly, worker!.MetadataToken, "SyncCatalogForLocaleAsync", "the SyncTypeLegAsync isolation try");
    }

    /// <summary>
    /// Asserts the whole plugin assembly contains exactly one call instruction
    /// to the given same-module methoddef token, naming every offender and its
    /// count in the failure message.
    /// </summary>
    private static void AssertSingleCallSite(Assembly assembly, int targetToken, string targetName, string expectedSite)
    {
        var callSites = IlCallScanner.DeclaredMethods(assembly)
            .Select(pair => (pair.Type, pair.Method,
                Count: IlCallScanner.CallTokens(pair.Method).Count(token => token == targetToken)))
            .Where(site => site.Count > 0)
            .ToList();
        int totalCalls = callSites.Sum(site => site.Count);

        Assert.True(
            totalCalls == 1,
            $"{targetName} must be called from exactly one place: {expectedSite}. "
            + $"Call instructions found: {totalCalls} across [{string.Join(", ", callSites.Select(s => $"{s.Type.FullName}.{s.Method.Name} x{s.Count}"))}]");
    }
}
