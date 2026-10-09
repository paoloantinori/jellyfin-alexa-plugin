using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// JF-800: the JF-792 parallel-phase contract as a mechanical roster test (the
/// <see cref="WarmingGateCoverageTests"/> idiom over the shared
/// <see cref="IlCallScanner"/>). The contract itself is the prose at the top of
/// <see cref="PluginCollection"/>: classes WITHOUT a collection attribute run in
/// the parallel phase, so they must touch none of the shared process-global
/// statics; the repo has documented history of exactly this contract class rotting
/// (WarmingGateCoverageTests exists because a CLAUDE.md list and a comment had both
/// gone stale, JF-465) and the JF-792 audit found it pre-violated on arrival
/// (DeadMicSweepElicitTests inherited PluginTestBase uncollected).
///
/// ROSTER (the JF-800 task prose said "every class WITHOUT
/// <c>[Collection("Plugin")]</c>"; the PluginCollection contract says "classes
/// WITHOUT a collection attribute"; the guard follows the CONTRACT and the
/// MECHANISM): the risk an uncollected class poses is racing the PARALLEL PHASE,
/// and xUnit runs DisableParallelization collections only after every parallel
/// collection finished, one at a time (verified at source, v2-2.7.0, the JF-792
/// note), so members of TimingSolo can race neither the parallel phase nor the
/// Plugin collection; flagging them would be false positives. The roster is
/// therefore every class with NO collection attribute (self or test-assembly
/// base chain) resolving to an EXCLUSIVE collection: a
/// <c>[CollectionDefinition]</c> with <c>DisableParallelization = true</c>. A
/// collection WITHOUT that flag still runs concurrently with every other
/// parallel collection, so a member of it IS part of the parallel phase and
/// stays in the roster (the code-review F1 hole: grouping a class into a new
/// plain collection must not silently exempt it). An unknown collection name
/// cannot be proven exclusive and keeps the class in the roster. The
/// PluginTestBase-membership rule is broader than the roster on purpose: every
/// PluginTestBase assignee must carry <c>[Collection("Plugin")]</c>
/// specifically (its ctor resets the shared statics, and the PluginTestBase
/// doc demands exactly that collection), so a hypothetical assignee parked in
/// some other collection still reds.
///
/// SURFACE (the type roster is CLOSED BY THE INVENTORY: the guard enforces
/// exactly the six type families the PluginCollection inventory names, so the
/// guard and the inventory agree; extending the contract with a new shared-static
/// type, or a shared-state static FIELD SHAPE beyond writable-or-container (see
/// <see cref="IsSharedStateField"/>), means editing the inventory prose AND
/// this file in the same change, the pointer comment in PluginCollection.cs
/// names this test as the enforcement).
/// Within the rostered types the surface is open-world, derived per family:
/// <list type="bullet">
/// <item>"Plugin.Instance (static singleton)" becomes the Plugin-declared STATIC
/// methods that reference the singleton state: seeded by every static method
/// whose body references a shared-state static field of Plugin (the auto-property
/// backing field, the fallback HttpClient), closed over same-type static calls
/// (ResetInstance assigns through the setter; the HttpClient getters read the
/// singleton), minus the licensed pair below. Derived, so a future Plugin static
/// reading the singleton joins automatically; compiler-generated nested bodies
/// (an async static's state machine, a static method's lambdas) are attributed
/// to their logical owner name, so a future STATIC ASYNC gate-touching helper
/// joins too (the stub's own body has no field access).</item>
/// <item>QueueContinuationStore, RadioModeState, PlaybackReportOrdering, and
/// SkillMaintenanceScope are static classes whose every member is shared
/// state: whole-type match.</item>
/// <item>"the VideoAudioController encode-gate/registry statics" becomes the
/// derived set of VideoAudioController STATIC methods that transitively
/// reference a shared-state static field (the encode gate, the transcode slot,
/// the encode-generation registries, the live-speed-process registry, the
/// gate-capacity twin); the JF-800 task's named three are pinned by a floor
/// fact so the derivation cannot silently shrink. The pure ffmpeg/playlist
/// builders reference no static field and exclude themselves.</item>
/// <item>StreamTokenHelper minting (named in the JF-800 task text) is
/// deliberately NOT on the surface: the class is pure static (no fields; the
/// secret is always a parameter), so minting races nothing. The phrase comes
/// from the VideoAudioControllerPureTests transitive membership rule, where
/// minting marked the Plugin.Instance-reading URL builders; that read is caught
/// by the Plugin rule here.</item>
/// </list>
///
/// SCAN DEPTH: poison is METHOD-grained. A method is poisoned when its own body
/// calls a surface member, or calls a poisoned test-assembly method (a fixpoint
/// over resolved call/callvirt/newobj edges; constructor edges carry inheritance,
/// so a derived class whose base ctor resets statics is caught). A roster class
/// reds when any method of its nested-type closure is poisoned. This is the
/// drift shape that matters: an uncollected class calling a TestHelpers
/// Plugin-swap member reds, while the many uncollected callers of TestHelpers'
/// clean members do not. Calls INTO PRODUCTION CODE are licensed OUT one level
/// deep, reads and writes alike (the VideoAudioControllerPureTests precedent:
/// a locally constructed controller's null-tolerant Plugin.Instance gate sync):
/// only a DIRECT call to a surface member reds, so a production facade that
/// internally writes a surface static is an unguarded residual (the JF-792
/// audit's own residual-risk class). At the one seam where that license lives
/// inside a Plugin static's own body, it is applied explicitly: the config
/// migrations named in <see cref="LicensedPluginSingletonReads"/>. Also
/// licensed OUT (the JF-792 audit's benign classes): class-internal statics
/// (xUnit never runs one class's tests concurrently), PluginTempDirSweeper's
/// additive temp-dir registry, and the SmapiManagement.RawModelClientOverrideForTests
/// single-user seam. Known boundaries, loud-only per the scanner's philosophy:
/// interface/virtual dispatch resolves to the compile-time member (an
/// implementation behind an interface escapes), generic (MethodSpec) call sites
/// are unresolvable and skipped, delegate targets (ldftn) are outside the walk,
/// and xUnit's reflection-invoked Dispose runs harness bodies no caller IL names
/// (both harnesses' Disposes are static-free today).
/// </summary>
public class ParallelPhaseStaticSurfaceTests
{
    /// <summary>
    /// The static classes whose EVERY member is shared state (the
    /// PluginCollection inventory names the types whole): a call/callvirt to
    /// any member declared on one of these is a surface touch.
    /// </summary>
    private static readonly HashSet<Type> WholeTypeSharedStatics = new()
    {
        typeof(QueueContinuationStore),
        typeof(RadioModeState),
        typeof(PlaybackReportOrdering),
        typeof(SkillMaintenanceScope)
    };

    /// <summary>
    /// The licensed singleton reads at the Plugin seam: the two config
    /// migrations read <c>Instance</c> null-tolerantly (and persist only a
    /// config the CALLER passed in), the production-code license applied at the
    /// one seam that lives inside a Plugin static; at parallel-phase time the
    /// singleton is null (this guard guarantees no uncollected writer), so the
    /// read is inert. A rename fails the floor fact so the license cannot rot
    /// into a stale no-op.
    /// </summary>
    private static readonly HashSet<string> LicensedPluginSingletonReads = new()
    {
        "MigrateDefaultInvocationNames",
        "MigrateStaleCacheCapDefault"
    };

    /// <summary>
    /// The collection names defined with DisableParallelization in this
    /// assembly (the exclusive collections; xUnit runs them only after every
    /// parallel collection finished). Computed from
    /// <see cref="CustomAttributeData"/> because the flag is a named argument.
    /// </summary>
    private static readonly Lazy<Dictionary<string, bool>> ExclusiveCollections =
        new(() =>
        {
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (Type type in typeof(ParallelPhaseStaticSurfaceTests).Assembly.GetTypes())
            {
                foreach (CustomAttributeData data in CustomAttributeData.GetCustomAttributes(type))
                {
                    if (typeof(CollectionDefinitionAttribute).IsAssignableFrom(data.Constructor.DeclaringType)
                        && data.ConstructorArguments.Count == 1
                        && data.ConstructorArguments[0].Value is string name)
                    {
                        map[name] = data.NamedArguments.Any(n =>
                            n.MemberName == "DisableParallelization" && n.TypedValue.Value is true);
                    }
                }
            }

            return map;
        });

    /// <summary>
    /// The derived VideoAudioController encode-static member names (raw
    /// accessor names included, e.g. get_EncodeGateConfiguredCapacityForTest),
    /// computed once. See the class doc's SURFACE section for the derivation.
    /// </summary>
    private static readonly Lazy<HashSet<string>> VideoAudioEncodeStaticNames =
        new(() => DeriveSharedStaticMemberNames(typeof(VideoAudioController)));

    /// <summary>
    /// The full derived Plugin static set (licenses still included), so the
    /// floor fact can assert the licensed names are members and not stale
    /// no-ops.
    /// </summary>
    private static readonly Lazy<HashSet<string>> PluginDerivedAllNames =
        new(() => DeriveSharedStaticMemberNames(typeof(Plugin)));

    /// <summary>
    /// The derived Plugin singleton-surface member names (the derived set MINUS
    /// <see cref="LicensedPluginSingletonReads"/>), computed once.
    /// </summary>
    private static readonly Lazy<HashSet<string>> PluginStaticNames =
        new(() => PluginDerivedAllNames.Value
            .Except(LicensedPluginSingletonReads)
            .ToHashSet());

    [Fact]
    public void UncollectedTestClasses_TouchNoSharedStaticSurface()
    {
        Assembly testAssembly = typeof(ParallelPhaseStaticSurfaceTests).Assembly;
        Module testModule = testAssembly.ManifestModule;

        // Method-grained scan (the scanner's own flat assembly walk): every
        // declared method with its direct surface hits and its resolved
        // test-assembly callee methods.
        var methodScans = new Dictionary<MethodBase, MethodScan>();
        foreach ((_, MethodBase method) in IlCallScanner.DeclaredMethods(testAssembly))
        {
            methodScans[method] = ScanMethod(method, testModule, testAssembly);
        }

        // Poison fixpoint: a method is poisoned when its own body touches the
        // surface, or when it calls a poisoned test-assembly method.
        var poisoned = methodScans
            .Where(kv => kv.Value.DirectSurfaceMembers.Count > 0)
            .Select(kv => kv.Key)
            .ToHashSet();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((MethodBase method, MethodScan scan) in methodScans)
            {
                if (poisoned.Contains(method) || !scan.CalleeMethods.Any(poisoned.Contains))
                {
                    continue;
                }

                poisoned.Add(method);
                changed = true;
            }
        }

        // The roster query, indexed by top-level type so the closure is not
        // re-enumerated per class.
        var poisonedByTopLevelType = poisoned
            .GroupBy(m => IlCallScanner.TopLevelType(m.DeclaringType!))
            .ToDictionary(g => g.Key, g => g.ToList());

        var failures = new List<string>();
        foreach (Type type in testAssembly.GetTypes())
        {
            if (!IsTestClass(type))
            {
                continue;
            }

            if (typeof(PluginTestBase).IsAssignableFrom(type) && !CarriesPluginCollection(type))
            {
                failures.Add(
                    $"{type.Name} inherits PluginTestBase but does not carry [Collection(\"Plugin\")] "
                    + "(the per-test ctor resets the shared statics; the PluginTestBase doc requires exactly that collection)");
                continue;
            }

            if (!HasExclusiveCollectionAttribute(type)
                && poisonedByTopLevelType.TryGetValue(IlCallScanner.TopLevelType(type), out List<MethodBase>? poisonedMethods))
            {
                failures.Add(
                    $"{type.Name} runs in the parallel phase (no exclusive collection attribute) but reaches the shared static surface: "
                    + DescribePoisonSites(type, poisonedMethods, methodScans, poisoned));
            }
        }

        Assert.True(
            failures.Count == 0,
            "Parallel-phase static-surface contract (the PluginCollection.cs prose) violated. "
            + "Either add [Collection(\"Plugin\")] (or TimingSolo for static-free wall-clock classes), "
            + "or remove the shared-static touch. Violations: "
            + string.Join(" | ", failures));
    }

    /// <summary>
    /// The derivation floors: the JF-800 task names three VideoAudioController
    /// seams and the Plugin singleton pair explicitly, and the licensed Plugin
    /// migrations must still be members of the derived set (a rename here fails,
    /// so the license cannot rot into a stale no-op). If the field-reference
    /// derivation ever stops surfacing the pins (a rename, a refactor behind an
    /// interface), this fails loudly instead of leaving a silently shrunken
    /// surface.
    /// </summary>
    [Fact]
    public void DerivedSurface_ContainsThePinnedMembers()
    {
        Assert.Contains("get_Instance", PluginStaticNames.Value);
        Assert.Contains("ResetInstance", PluginStaticNames.Value);
        Assert.Contains("get_HttpClient", PluginStaticNames.Value);
        Assert.Contains("get_HttpClientProgressive", PluginStaticNames.Value);

        foreach (string licensed in LicensedPluginSingletonReads)
        {
            Assert.True(
                PluginDerivedAllNames.Value.Contains(licensed),
                $"{licensed} is licensed in LicensedPluginSingletonReads but the derivation no longer sees it; update the license list");

            // The license matches by NAME, so a future OVERLOAD of a licensed
            // name would silently inherit it (code-review F4); pin today's
            // arity so the overload reds and forces a conscious decision.
            Assert.Single(
                typeof(Plugin).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == licensed));
        }

        HashSet<string> display = VideoAudioEncodeStaticNames.Value
            .Select(AccessorDisplayName)
            .ToHashSet();
        Assert.Contains("UpdateEncodeGateCapacity", display);
        Assert.Contains("LiveSpeedEncodeCacheKeysForTest", display);
        Assert.Contains("EncodeGateConfiguredCapacityForTest", display);
    }

    /// <summary>
    /// The test-assembly callees a method's own body reaches: direct
    /// shared-static surface hits (plugin-assembly members) and resolved
    /// test-assembly callee methods (the poison vectors).
    /// </summary>
    private sealed class MethodScan
    {
        public HashSet<string> DirectSurfaceMembers { get; } = new(StringComparer.Ordinal);

        public HashSet<MethodBase> CalleeMethods { get; } = new();
    }

    private static MethodScan ScanMethod(MethodBase method, Module module, Assembly testAssembly)
    {
        var scan = new MethodScan();
        // One instruction walk serves both token families (call, callvirt,
        // newobj all carry 4-byte member tokens).
        foreach ((_, int token) in IlCallScanner.InstructionOperands(method, 0x28, 0x6F, 0x73))
        {
            if (IlCallScanner.TryResolveMethod(module, token) is not { } callee)
            {
                continue;
            }

            if (IsSharedStaticSurfaceMethod(callee))
            {
                scan.DirectSurfaceMembers.Add($"{callee.DeclaringType!.Name}.{callee.Name}");
            }
            else if (callee.DeclaringType?.Assembly == testAssembly)
            {
                scan.CalleeMethods.Add(callee);
            }
        }

        return scan;
    }

    /// <summary>
    /// True when the resolved callee is a member of the shared process-global
    /// surface: a whole-type static class, a derived Plugin singleton member,
    /// or a derived VideoAudioController encode static.
    /// </summary>
    private static bool IsSharedStaticSurfaceMethod(MethodBase callee)
        => WholeTypeSharedStatics.Contains(callee.DeclaringType)
            || (callee.DeclaringType == typeof(Plugin) && PluginStaticNames.Value.Contains(callee.Name))
            || (callee.DeclaringType == typeof(VideoAudioController) && VideoAudioEncodeStaticNames.Value.Contains(callee.Name));

    /// <summary>
    /// The shared-static member names a type exposes through its own STATIC
    /// methods: seeded by every static method whose body references a
    /// shared-state static field of the type (writable, or a stateful shared
    /// container: HttpClient, SemaphoreSlim, ConcurrentDictionary), closed over
    /// same-type static calls so a helper delegating to a touching member joins
    /// (ResetInstance assigns through the Instance setter). Each method's IL is
    /// decoded ONCE: the single opcode walk snapshots its field hits and
    /// same-type static callees, and the fixpoint iterates snapshots, not
    /// bodies. PURE statics (no static field reference, e.g. the Plugin config
    /// migrations that only read the singleton getter, and the ffmpeg argument
    /// builders) seed nothing; the migrations still join via the closure and
    /// are then licensed out by the caller, the builders never join at all.
    /// </summary>
    private static HashSet<string> DeriveSharedStaticMemberNames(Type type)
    {
        HashSet<string> sharedFieldNames = type
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(IsSharedStateField)
            .Select(f => f.Name)
            .ToHashSet();

        // The per-method snapshot: (logical name, references a shared field,
        // calls same-type statics with these names). The scanner's own boundary
        // decode is driven directly (one decode per body, opcode
        // discrimination included): call/callvirt name members,
        // ldsfld/ldsflda/stsfld name fields. Compiler-generated nested bodies
        // (async state machines, lambdas) are attributed to their logical
        // owner name, so a STATIC ASYNC helper whose MoveNext touches a shared
        // field seeds the callable stub's name (the stub body itself has no
        // field access); plain methods of real nested types keep their own
        // name and are NOT part of any outer member.
        var snapshots = new List<(string Name, bool ReferencesSharedField, HashSet<string> SameTypeStaticCallees)>();
        foreach (Type closureType in IlCallScanner.NestedTypeClosure(type))
        {
            foreach (MethodBase method in IlCallScanner.DeclaredCallableMethods(closureType))
            {
                string logicalName;
                if (closureType == type)
                {
                    if (method is not MethodInfo { IsStatic: true })
                    {
                        continue;
                    }

                    logicalName = method.Name;
                }
                else
                {
                    logicalName = IlCallScanner.LogicalMethodName(method);
                    if (logicalName == method.Name)
                    {
                        continue;
                    }
                }

                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null)
                {
                    continue;
                }

                bool referencesSharedField = false;
                var sameTypeCallees = new HashSet<string>(StringComparer.Ordinal);
                foreach ((_, short opcodeValue, int operandStart, _) in IlCallScanner.Instructions(il))
                {
                    if (opcodeValue < 0)
                    {
                        continue;
                    }

                    byte opcode = (byte)opcodeValue;
                    if (opcode is not (0x28 or 0x6F or 0x7E or 0x7F or 0x80))
                    {
                        continue;
                    }

                    int token = BitConverter.ToInt32(il, operandStart);
                    if (opcode is 0x7E or 0x7F or 0x80)
                    {
                        FieldInfo? field;
                        try
                        {
                            field = type.Module.ResolveField(token, null, null);
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }

                        if (field?.DeclaringType == type && sharedFieldNames.Contains(field.Name))
                        {
                            referencesSharedField = true;
                        }
                    }
                    else if (IlCallScanner.TryResolveMethod(type.Module, token) is { } callee
                        && callee.DeclaringType == type
                        && callee is MethodInfo { IsStatic: true })
                    {
                        sameTypeCallees.Add(callee.Name);
                    }
                }

                snapshots.Add((logicalName, referencesSharedField, sameTypeCallees));
            }
        }

        var names = snapshots
            .Where(s => s.ReferencesSharedField)
            .Select(s => s.Name)
            .ToHashSet();

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach ((string name, bool _, HashSet<string> callees) in snapshots)
            {
                if (!names.Contains(name) && callees.Overlaps(names))
                {
                    names.Add(name);
                    changed = true;
                }
            }
        }

        return names;
    }

    /// <summary>
    /// A static field is shared state when it is writable (the singleton
    /// backing field, the gate-capacity twin) or a stateful shared container
    /// the readonly keyword cannot make safe (a live HttpClient, a semaphore,
    /// a concurrent registry dictionary). Static readonly strings and other
    /// immutable data are not. Callers enumerate with
    /// <see cref="BindingFlags.Static"/>; consts are excluded via
    /// <see cref="FieldInfo.IsLiteral"/> because GetFields returns them.
    /// </summary>
    private static bool IsSharedStateField(FieldInfo field)
        => !field.IsLiteral
            && (!field.IsInitOnly
                || field.FieldType == typeof(HttpClient)
                || typeof(SemaphoreSlim).IsAssignableFrom(field.FieldType)
                || (field.FieldType.IsGenericType
                    && field.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>)));

    /// <summary>
    /// The poisoned methods of the type's nested-type closure (its own declared
    /// surface; inherited methods are reached through the constructor and
    /// explicit-call edges, which is execution-accurate), one chain each,
    /// capped so a badly drifted class still produces a readable message.
    /// </summary>
    private static string DescribePoisonSites(
        Type type,
        List<MethodBase> poisonedMethods,
        Dictionary<MethodBase, MethodScan> methodScans,
        HashSet<MethodBase> poisoned)
    {
        List<string> sites = poisonedMethods
            .GroupBy(m => IlCallScanner.LogicalMethodName(m))
            .Select(g => DescribePoisonPath(type, g.First(), methodScans, poisoned))
            .Take(6)
            .ToList();
        return sites.Count > 0
            ? string.Join(" | ", sites)
            : "no chain (guard bug: a poisoned method left no site)";
    }

    /// <summary>
    /// The human-facing member name for an accessor (get_X reports X), for
    /// floor assertions that name properties.
    /// </summary>
    private static string AccessorDisplayName(string name)
        => name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal)
            ? name["get_".Length..]
            : name;

    private static bool IsTestClass(Type type)
        => !type.IsAbstract
            && type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Any(m => m.GetCustomAttributes(inherit: false).Any(a => a is FactAttribute));

    /// <summary>
    /// The collection names on the type's base chain (xUnit resolves the
    /// attribute inherit-aware on classes, so a derived class of a collected
    /// class is collected). xUnit's CollectionAttribute exposes the name only
    /// as the constructor argument (the runner reads ctor args the same way),
    /// so the names come from <see cref="CustomAttributeData"/>, not a
    /// property.
    /// </summary>
    private static IEnumerable<string> BaseChainCollectionNames(Type type)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            foreach (CustomAttributeData data in CustomAttributeData.GetCustomAttributes(t))
            {
                if (typeof(CollectionAttribute).IsAssignableFrom(data.Constructor.DeclaringType)
                    && data.ConstructorArguments.Count == 1
                    && data.ConstructorArguments[0].Value is string name)
                {
                    yield return name;
                }
            }
        }
    }

    private static bool HasExclusiveCollectionAttribute(Type type)
    {
        foreach (string name in BaseChainCollectionNames(type))
        {
            // An unknown definition cannot be proven exclusive (xUnit itself
            // would fail to build the collection at runtime); conservative:
            // the class stays in the roster.
            if (ExclusiveCollections.Value.TryGetValue(name, out bool exclusive) && exclusive)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CarriesPluginCollection(Type type)
        => BaseChainCollectionNames(type).Contains("Plugin");

    /// <summary>
    /// One representative chain from a roster class through a poisoned method
    /// to the direct surface touch (BFS over the poisoned callee edges), so
    /// the failure names the exact vector to fix.
    /// </summary>
    private static string DescribePoisonPath(
        Type rosterType,
        MethodBase poisonedMethod,
        Dictionary<MethodBase, MethodScan> methodScans,
        HashSet<MethodBase> poisoned)
    {
        var parents = new Dictionary<MethodBase, MethodBase> { [poisonedMethod] = poisonedMethod };
        var queue = new Queue<MethodBase>();
        queue.Enqueue(poisonedMethod);
        while (queue.Count > 0)
        {
            MethodBase current = queue.Dequeue();
            MethodScan scan = methodScans[current];
            if (scan.DirectSurfaceMembers.Count > 0)
            {
                // Walk poisonedMethod -> ... -> current, then read the chain
                // roster-first; one frame per hop, cap the message.
                var walk = new List<string>();
                for (MethodBase? step = current; step != poisonedMethod; step = parents[step])
                {
                    walk.Add($"{IlCallScanner.TopLevelType(step!.DeclaringType!).Name}.{IlCallScanner.LogicalMethodName(step)}");
                }

                walk.Reverse();
                walk.Insert(0, $"{rosterType.Name}.{IlCallScanner.LogicalMethodName(poisonedMethod)}");
                return $"{string.Join(" -> ", walk.Take(5))}{(walk.Count > 5 ? " -> ..." : string.Empty)} "
                    + $"reaches {string.Join(", ", scan.DirectSurfaceMembers)}";
            }

            foreach (MethodBase callee in scan.CalleeMethods.Where(poisoned.Contains))
            {
                if (parents.TryAdd(callee, current))
                {
                    queue.Enqueue(callee);
                }
            }
        }

        return "poisoned via a path the walk could not reconstruct (fixpoint and BFS disagree; treat as a guard bug)";
    }
}
