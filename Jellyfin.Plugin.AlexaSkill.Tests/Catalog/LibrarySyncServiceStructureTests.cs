#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-706/JF-716 structural pins for the catalog-sync per-type leg wiring.
/// Standalone collection-free IL-scan class (no plugin fixture needed),
/// following the WarmingGateCoverageTests / CatalogValueFactoryTests
/// discipline: shared IlCallScanner, loud-only failure. Every catalog type
/// must flow through the ONE SyncTypeLegAsync call site (the loop over the
/// per-type wiring table in RunLegAsync, which the injection gate and the
/// UpdateInteractionModelAsync extraction derive from) and onward through the
/// ONE SyncCatalogForLocaleAsync call site inside it, so the JF-695
/// freeze-isolation try, the TypeLegEntryProbeForTest seam, and the
/// minted/id-forwarding bookkeeping cannot be bypassed for any type. JF-716
/// adds the stored-id closure pin over the three User catalog-id accessors
/// (see StoredCatalogIdAccessors_AreCalledOnlyByTheWiringTableLambdas).
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
        Assert.True(
            worker != null,
            "SyncCatalogForLocaleAsync must still exist as a private instance method of LibrarySyncService (this pin's lookup target)");

        AssertSingleCallSite(assembly, worker!.MetadataToken, "SyncCatalogForLocaleAsync", "the SyncTypeLegAsync isolation try");
    }

    /// <summary>
    /// JF-716: the user's three stored catalog ids (the auto-properties
    /// User.ArtistCatalogId / AlbumCatalogId / SeriesCatalogId) are each read
    /// and written from EXACTLY ONE place in the whole plugin assembly: a row
    /// lambda of the JF-706 wiring table. Closes the seam the two pins above
    /// cannot see: re-expanding the six positional UpdateInteractionModelAsync
    /// arguments or the injection gate into hand per-type ternaries (the
    /// pre-JF-706 shape, which READS the getters at the call site), reverting
    /// JF-711's setter threading back to a CatalogType-keyed if/else write-back
    /// inside SyncCatalogForLocaleAsync (which WRITES the setters there), or
    /// any other out-of-table enumeration of a stored id all add a second call
    /// instruction on an accessor or move its only caller off a table lambda,
    /// and fail this scan with the offender named. Grep-verified at JF-711:
    /// nothing outside the table rows touches the three ids, so the
    /// whole-assembly assertion holds today and any future read/write anywhere
    /// in the plugin must consciously widen this pin.
    /// SCAN OPCODE (why no ldfld/stfld): the stored ids are AUTO-PROPERTIES,
    /// so every C# use-site shape (table lambda, hand ternary, if/else
    /// write-back) compiles to a call/callvirt on the accessor methoddef,
    /// which IlCallScanner.CallTokens already reports; probe-verified there is
    /// no 0x7B/0x7D operand naming a User catalog-id backing field anywhere in
    /// LibrarySyncService (C# cannot name a backing field outside its type),
    /// so a field-opcode scan keyed on these ids can never fire on any C#
    /// source shape and is deliberately NOT added. Converting the ids to real
    /// fields still fails loudly here at the conversion itself: the accessors
    /// vanish, so the GetProperty lookup below returns null.
    /// BOUNDARY (the JF-706 pin-2 precedent): a read or write via REFLECTION
    /// (GetProperty("...")/GetValue) or an expression-tree capture emits no
    /// accessor call instruction and evades this scan; that is the
    /// deliberate-evasion class, accepted exactly like the
    /// method-group/reflection boundary the JF-706 pin documents. The shape
    /// check accepts any lambda compiled under SyncUserLibraryAsync, so an
    /// ad-hoc lambda wrapping a hand enumeration inside that same method also
    /// passes at count 1; every realistic re-expansion (direct ternary,
    /// write-back, helper hoist) compiles outside lambdas and fails. The scan
    /// walks the plugin assembly only, so test-assembly uses are out of scope.
    /// </summary>
    [Fact]
    public void StoredCatalogIdAccessors_AreCalledOnlyByTheWiringTableLambdas()
    {
        // ONE assembly walk shared by all six accessor checks: matching six
        // methoddef tokens in memory beats six full IL walks (the walk's only
        // product is the per-method token array).
        var methodCalls = MethodCallTokens(typeof(LibrarySyncService).Assembly);

        // Gate-marker tail: the list is DERIVED (every User property ending in
            // CatalogId) so a fourth synced type's stored id - the exact one-row
            // edit JF-711 made compile-loud - joins the pin automatically
            // instead of arriving unpinned (grep-verified: the suffix matches
            // exactly Artist/Album/Series today).
            foreach (PropertyInfo property in typeof(User)
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.Name.EndsWith("CatalogId", StringComparison.Ordinal))
                         .OrderBy(p => p.Name))
        {
            string propertyName = property.Name;
            Assert.True(
                property != null,
                $"User.{propertyName} must exist as a public instance property: a rename, or a conversion to a plain field, defuses this pin and must be a conscious edit");

            // Gate-marker tail: nonPublic so a future non-public accessor refactor
            // (e.g. internal set) keeps its coverage instead of silently
            // dropping out of the pin (probed: the parameterless overload
            // returns public accessors only).
            foreach (MethodInfo accessor in property.GetAccessors(true))
            {
                AssertOnlyCallerIsTableLambda(methodCalls, accessor);
            }
        }
    }

    /// <summary>
    /// Asserts the accessor's only call instruction in the whole plugin
    /// assembly sits in one wiring-table row lambda, naming every offender and
    /// count in the failure message.
    /// </summary>
    private static void AssertOnlyCallerIsTableLambda(
        IReadOnlyList<(Type Type, MethodBase Method, int[] Tokens)> methodCalls,
        MethodInfo accessor)
    {
        var callSites = CallSites(methodCalls, accessor.MetadataToken);

        // Sum==1 is exactly one site with one instruction (the list holds only
        // Count > 0 sites), the same idiom AssertSingleCallSite reads.
        int totalCalls = callSites.Sum(site => site.Count);
        // Gate-marker tail: the message names WHICH conjunct failed - a count of
        // 1 next to "exactly one place" reads as a passing count when the shape
        // half is the actual failure (the helper-hoist sabotage shape).
        Assert.True(
            totalCalls == 1 && IsWiringTableLambda(callSites[0].Method),
            $"{accessor.Name} must be called from exactly one place: a lambda compiled in LibrarySyncService.SyncUserLibraryAsync (the JF-706 wiring-table rows' shape). "
            + (totalCalls != 1
                ? $"Found {totalCalls} call instructions. "
                : "The single call site is not the wiring-table lambda shape. ")
            + $"Call sites found: [{DescribeCallSites(callSites)}]");
    }

    /// <summary>
    /// A wiring-table row lambda: Roslyn compiles each row's accessors to a
    /// display-class method named "&lt;SyncUserLibraryAsync&gt;b__N" (the b__
    /// LAMBDA marker; local functions carry g__, async state machines d__ and
    /// expose MoveNext), nested under LibrarySyncService. The realistic
    /// re-expansions compile elsewhere by construction: a hand ternary at the
    /// UpdateInteractionModelAsync call lands in RunLegAsync's state-machine
    /// MoveNext, and the pre-JF-711 if/else write-back lands in
    /// SyncCatalogForLocaleAsync's. Neither is a b__ lambda, so the shape
    /// check fails them even where a count-only check would stay at one.
    /// </summary>
    private static bool IsWiringTableLambda(MethodBase method) =>
        method.DeclaringType != null
        && IlCallScanner.TopLevelType(method.DeclaringType) == typeof(LibrarySyncService)
        && method.Name.StartsWith("<SyncUserLibraryAsync>b__", StringComparison.Ordinal);

    /// <summary>
    /// Asserts the whole plugin assembly contains exactly one call instruction
    /// to the given same-module methoddef token, naming every offender and its
    /// count in the failure message.
    /// </summary>
    private static void AssertSingleCallSite(Assembly assembly, int targetToken, string targetName, string expectedSite)
    {
        var callSites = CallSites(assembly, targetToken);
        int totalCalls = callSites.Sum(site => site.Count);

        Assert.True(
            totalCalls == 1,
            $"{targetName} must be called from exactly one place: {expectedSite}. "
            + $"Call instructions found: {totalCalls} across [{DescribeCallSites(callSites)}]");
    }

    /// <summary>
    /// Every method in the assembly that calls the given same-module methoddef
    /// token, with its call-instruction count (JF-716 dedup: the shared walk of
    /// the JF-706 single-call-site assertion and the wiring-table-lambda
    /// assertion above).
    /// </summary>
    private static List<(Type Type, MethodBase Method, int Count)> CallSites(Assembly assembly, int targetToken)
        => CallSites(MethodCallTokens(assembly), targetToken);

    /// <summary>
    /// The same projection over a pre-walked method/token list, so one
    /// <see cref="MethodCallTokens"/> walk can serve many target tokens (the
    /// six stored-id accessors).
    /// </summary>
    private static List<(Type Type, MethodBase Method, int Count)> CallSites(
        IEnumerable<(Type Type, MethodBase Method, int[] Tokens)> methodCalls,
        int targetToken)
        => methodCalls
            .Select(pair => (pair.Type, pair.Method,
                Count: pair.Tokens.Count(token => token == targetToken)))
            .Where(site => site.Count > 0)
            .ToList();

    /// <summary>
    /// Every declared method in the assembly paired with its call-instruction
    /// tokens, the ONE IL walk the call-site assertions match tokens against.
    /// </summary>
    private static List<(Type Type, MethodBase Method, int[] Tokens)> MethodCallTokens(Assembly assembly)
        => IlCallScanner.DeclaredMethods(assembly)
            .Select(pair => (pair.Type, pair.Method, Tokens: IlCallScanner.CallTokens(pair.Method).ToArray()))
            .ToList();

    /// <summary>
    /// The call-site list both failure messages print (the one rendering of
    /// the walk's result, so the assertions cannot drift). Raw
    /// compiler-generated names are deliberate: the closure shape IS the
    /// pinned fact, so no logical-name mapping is applied.
    /// </summary>
    private static string DescribeCallSites(IEnumerable<(Type Type, MethodBase Method, int Count)> callSites)
        => string.Join(", ", callSites.Select(s => $"{s.Type.FullName}.{s.Method.Name}{DecodeCompilerName(s.Method.Name)} x{s.Count}"));

    /// <summary>
    /// Gate-marker tail: a parenthesized source-level hint for the Roslyn
    /// mangled shapes (g__ local function, b__ lambda, d__ state machine), so a
    /// triager can find the site without knowing the conventions; the raw name
    /// stays the pinned fact.
    /// </summary>
    private static string DecodeCompilerName(string name)
    {
        int g = name.IndexOf("g__", StringComparison.Ordinal);
        if (g >= 0)
        {
            int end = name.IndexOf('|', g);
            return $" (local function {name.Substring(g + 3, (end < 0 ? name.Length : end) - g - 3)})";
        }

        int b = name.IndexOf("b__", StringComparison.Ordinal);
        if (b >= 0)
        {
            return " (lambda)";
        }

        int d = name.IndexOf("d__", StringComparison.Ordinal);
        if (d >= 0)
        {
            return $" (state machine of {name.Substring(2, d - 2)})";
        }

        return string.Empty;
    }
}
