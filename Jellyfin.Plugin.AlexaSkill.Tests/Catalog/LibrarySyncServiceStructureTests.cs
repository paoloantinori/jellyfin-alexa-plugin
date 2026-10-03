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
/// JF-706/JF-716/JF-727 structural pins for the catalog-sync per-type leg
/// wiring. Standalone collection-free IL-scan class (no plugin fixture
/// needed), following the WarmingGateCoverageTests / CatalogValueFactoryTests
/// discipline: shared IlCallScanner, loud-only failure. Every catalog type
/// must flow through the ONE SyncTypeLegAsync call site (the loop over the
/// per-type wiring table in RunLegAsync, which the injection gate and the
/// UpdateInteractionModelAsync extraction derive from) and onward through the
/// ONE SyncCatalogForLocaleAsync call site inside it, so the JF-695
/// freeze-isolation try, the TypeLegEntryProbeForTest seam, and the
/// minted/id-forwarding bookkeeping cannot be bypassed for any type. JF-716
/// adds the stored-id closure pin over the three User catalog-id accessors
/// (see StoredCatalogIdAccessors_AreCalledOnlyByTheWiringTableLambdas).
/// JF-727 adds the sourcing pins: the single fetch call site and the
/// count-setter closure over the wiring table's StoreCount row lambdas.
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
    /// JF-727: the item sourcing is the single FetchLibraryItems call site:
    /// the loop over the per-type wiring table in SyncUserLibraryAsync fetches
    /// each row's items by the row's Kind. The pre-JF-727 block hand-wrote one
    /// fetch per type; re-expanding to hand fetches fails here first with the
    /// offender named, which is the loud guard for the block's behavioral
    /// half: a missed fetch leaves that row's Items empty and the type never
    /// syncs, silently. The pin binds the site to SyncUserLibraryAsync's own
    /// state machine (code-review F3), not just any single caller: the fetch
    /// must stay once-per-run in the method body (shared across locales);
    /// moving the loop into RunLegAsync would re-fetch per locale with a
    /// count-only pin still green. GATE-MARKER TAIL: the binding is
    /// METHOD-granular - a re-fetch loop moved INTO SyncUserLibraryAsync's own
    /// locale foreach keeps this pin green (same MoveNext method); the
    /// once-per-run contract is enforced only against moves OUT of the method
    /// body.
    /// </summary>
    [Fact]
    public void FetchLibraryItems_HasExactlyOneCallSite_TheSourcingLoop()
    {
        var assembly = typeof(LibrarySyncService).Assembly;

        MethodBase worker = RequireMethod(
            typeof(LibrarySyncService),
            "FetchLibraryItems",
            BindingFlags.NonPublic | BindingFlags.Instance,
            "a private instance method of LibrarySyncService");

        // The d__ prefix with this exact shape matches only the method's own
        // state machine; a local function's compiles as <<Owner>g__Name|i>d__,
        // so Single() cannot accidentally bind RunLegAsync's.
        Type stateMachine = typeof(LibrarySyncService)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Single(t => t.Name.StartsWith("<SyncUserLibraryAsync>d__", StringComparison.Ordinal));
        MethodBase sourcingLoop = RequireMethod(
            stateMachine,
            "MoveNext",
            BindingFlags.NonPublic | BindingFlags.Instance,
            "the state machine of SyncUserLibraryAsync");

        AssertOnlyCallerIs(
            assembly,
            worker,
            "FetchLibraryItems",
            sourcingLoop,
            "the JF-727 sourcing loop in SyncUserLibraryAsync (once per run, shared across locales)");
    }

    /// <summary>
    /// JF-727: the SyncResult per-type counts are written ONLY from wiring
    /// table row lambdas (the StoreCount element), the JF-716 stored-id
    /// closure idiom applied to the count setters. SETTERS ONLY: the count
    /// getters are read by the completion log and CatalogSyncTask, so the
    /// whole-accessor sweep the stored-id pin uses would be wrong here. The
    /// property list is DERIVED (every writable SyncResult *Count property)
    /// so a fourth synced type's count joins the pin automatically, with a
    /// subset guard keeping the rename tripwire loud; re-expanding the
    /// sourcing block's count assignments into hand-written per-type
    /// statements fails with the offender named (count or shape conjunct,
    /// the JF-716 failure-message discipline).
    /// </summary>
    [Fact]
    public void SyncResultCountSetters_AreCalledOnlyByTheWiringTableLambdas()
    {
        var methodCalls = MethodCallTokens(typeof(LibrarySyncService).Assembly);

        // One derivation shared by the subset guard and the setter sweep (the
        // two reads must not be two independently derived copies).
        List<PropertyInfo> countProperties = CountProperties();
        foreach (string expected in new[] { "ArtistCount", "AlbumCount", "SeriesCount" })
        {
            Assert.Contains(
                expected,
                countProperties.Select(p => p.Name));
        }

        foreach (PropertyInfo property in countProperties)
        {
            MethodInfo? setter = property.GetSetMethod(true);
            Assert.True(
                setter != null,
                $"{property.Name} must keep a setter (this pin's lookup target)");
            AssertOnlyCallerIsTableLambda(methodCalls, setter!);
        }
    }

    /// <summary>
    /// The derived count sweep both loops above iterate: every writable
    /// SyncResult property whose name ends in Count.
    /// GATE-MARKER TAIL: a future UNRELATED count property set outside the wiring
    /// table fails this pin (it already matches the suffix); the fix is narrowing
    /// or differentiating the predicate, or routing a sync-related count through a
    /// StoreCount row, judged per addition.
    /// </summary>
    private static List<PropertyInfo> CountProperties() =>
        typeof(SyncResult)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.Name.EndsWith("Count", StringComparison.Ordinal) && p.CanWrite)
            .OrderBy(p => p.Name)
            .ToList();

    /// <summary>
    /// Resolves a method a pin looks up by name, failing with the pin's
    /// lookup-target phrasing when it is gone (JF-727 simplify hoist: the diff
    /// added a second copy of the GetMethod + guard preamble; internal for the
    /// CatalogWiringGraft keyed-extraction pin, the third consumer).
    /// </summary>
    /// <param name="type">The type declaring the method.</param>
    /// <param name="name">The method name.</param>
    /// <param name="flags">The binding flags of the declared shape.</param>
    /// <param name="declaredAs">How the failure message describes the expected shape (e.g. "a private instance method of LibrarySyncService").</param>
    /// <returns>The resolved method.</returns>
    internal static MethodBase RequireMethod(Type type, string name, BindingFlags flags, string declaredAs)
    {
        MethodBase? method = type.GetMethod(name, flags);
        Assert.True(
            method != null,
            $"{type.Name}.{name} must still exist as {declaredAs} (this pin's lookup target)");
        return method!;
    }

    /// <summary>
    /// The cross-class form of the single-call-site assertion (JF-727 simplify
    /// hoist per the JF-582/634/699 no-private-copies discipline: the
    /// CatalogWiringGraft keyed-extraction pin needed the same whole-assembly
    /// walk and offender naming). Asserts BOTH dimensions the class's own pins
    /// split across helpers: exactly one call instruction, and that
    /// instruction sits in exactly the expected method. Internal for the same
    /// test assembly's other pin classes.
    /// </summary>
    /// <param name="assembly">The assembly whose IL to walk.</param>
    /// <param name="target">The call target the scan counts.</param>
    /// <param name="targetName">The target's name for the failure message.</param>
    /// <param name="expectedCaller">The one method allowed to call the target.</param>
    /// <param name="expectedSite">The rationale naming the expected site in the failure message.</param>
    internal static void AssertOnlyCallerIs(
        Assembly assembly,
        MethodBase target,
        string targetName,
        MethodBase expectedCaller,
        string expectedSite)
        => AssertExactlyOneCallSite(
            CallSites(assembly, target.MetadataToken),
            targetName,
            expectedSite,
            site => site.Method == expectedCaller && site.Type == expectedCaller.DeclaringType,
            "The single call site is not the expected method. ");

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
        // edit JF-711 made compile-loud - joins the pin automatically. The
        // subset guard below keeps the rename tripwire the old per-name
        // GetProperty lookup provided: an expected id dropping out of the
        // derived set (a rename, a plain-field conversion, a non-public
        // retreat) fails loudly, while additions join freely. NonPublic so a
        // non-public property cannot silently leave the sweep (the same
        // silent-loss class GetAccessors(true) closes at the accessor level).
        var derivedIds = typeof(User)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.Name.EndsWith("CatalogId", StringComparison.Ordinal))
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToList();
        foreach (string expected in new[] { "ArtistCatalogId", "AlbumCatalogId", "SeriesCatalogId" })
        {
            Assert.Contains(
                expected,
                derivedIds);
        }

        foreach (PropertyInfo property in typeof(User)
                         .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         .Where(p => p.Name.EndsWith("CatalogId", StringComparison.Ordinal))
                         .OrderBy(p => p.Name))
        {

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
        AssertExactlyOneCallSite(
            CallSites(methodCalls, accessor.MetadataToken),
            accessor.Name,
            "a lambda compiled in LibrarySyncService.SyncUserLibraryAsync (the JF-706 wiring-table rows' shape)",
            site => IsWiringTableLambda(site.Method),
            "The single call site is not the wiring-table lambda shape. ");
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
    /// The ONE assertion skeleton every call-site pin in this class reads
    /// (JF-727 code-review consolidation: the count-only, lambda-shape, and
    /// expected-caller variants had drifted into three hand-rolled copies of
    /// the walk/sum/message composition, the private-copies shape the
    /// JF-582/634/699 discipline exists to eliminate). Exactly one call
    /// instruction in the whole walk, plus the caller's optional site shape;
    /// the failure message names WHICH conjunct failed (the JF-716 gate-marker
    /// discipline: a count of 1 next to "exactly one place" reads as a passing
    /// count when the shape half is the actual failure).
    /// </summary>
    /// <param name="callSites">The target's call sites (the CallSites projection; only Count > 0 sites).</param>
    /// <param name="targetName">The target's name for the failure message.</param>
    /// <param name="expectedSite">The rationale naming the expected site in the failure message.</param>
    /// <param name="siteShape">Optional conjunct on the single call site (null for count-only pins).</param>
    /// <param name="shapeFailure">The message fragment for the shape conjunct's failure.</param>
    private static void AssertExactlyOneCallSite(
        List<(Type Type, MethodBase Method, int Count)> callSites,
        string targetName,
        string expectedSite,
        Func<(Type Type, MethodBase Method, int Count), bool>? siteShape,
        string shapeFailure)
    {
        // Sum==1 is exactly one site with one instruction (the list holds only
        // Count > 0 sites); the short-circuit order evaluates callSites[0]
        // only when totalCalls == 1, which implies the list is non-empty.
        int totalCalls = callSites.Sum(site => site.Count);

        Assert.True(
            totalCalls == 1 && (siteShape == null || siteShape(callSites[0])),
            $"{targetName} must be called from exactly one place: {expectedSite}. "
            + (totalCalls != 1
                ? $"Found {totalCalls} call instructions. "
                : shapeFailure)
            + $"Call sites found: [{DescribeCallSites(callSites)}]");
    }

    /// <summary>
    /// Asserts the whole plugin assembly contains exactly one call instruction
    /// to the given same-module methoddef token, naming every offender and its
    /// count in the failure message.
    /// </summary>
    private static void AssertSingleCallSite(Assembly assembly, int targetToken, string targetName, string expectedSite)
        => AssertExactlyOneCallSite(
            CallSites(assembly, targetToken),
            targetName,
            expectedSite,
            siteShape: null,
            shapeFailure: string.Empty);

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
        => string.Join(", ", callSites.Select(s => $"{s.Type.FullName}.{s.Method.Name}{DecodeCompilerName(s.Type, s.Method)} x{s.Count}"));

    /// <summary>
    /// Gate-marker tail (simplify round fix): a parenthesized source-level hint
    /// for the Roslyn mangled shapes so a triager can find the site without
    /// knowing the conventions. g__ (local function) and b__ (lambda) decode
    /// from the METHOD name; the d__ state-machine marker lives on the nested
    /// TYPE name while the method is MoveNext (empirically probed: zero method
    /// names carry d__), so that branch reads the declaring type. The mangled
    /// name stays the pinned fact; the mangling-convention OWNERSHIP lives on
    /// IlCallScanner (LogicalMethodName maps to owners; this annotates shapes).
    /// </summary>
    private static string DecodeCompilerName(Type type, MethodBase method)
    {
        int g = method.Name.IndexOf("g__", StringComparison.Ordinal);
        if (g >= 0)
        {
            int end = method.Name.IndexOf('|', g);
            return $" (local function {method.Name.Substring(g + 3, (end < 0 ? method.Name.Length : end) - g - 3)})";
        }

        if (method.Name.IndexOf("b__", StringComparison.Ordinal) >= 0)
        {
            return " (lambda)";
        }

        int d = type.Name.IndexOf("d__", StringComparison.Ordinal);
        if (d >= 0)
        {
            // "<Owner>d__N": the owner spans [1, d) (skip the leading '<').
            return $" (state machine of {type.Name.Substring(1, d - 1)})";
        }

        return string.Empty;
    }
}
