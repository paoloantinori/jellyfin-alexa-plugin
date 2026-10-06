using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;
using QueueContinuationStore = Jellyfin.Plugin.AlexaSkill.Alexa.QueueContinuationStore;
using RadioModeState = Jellyfin.Plugin.AlexaSkill.Alexa.RadioModeState;

// Parallel execution model (JF-792; the assembly-level DisableTestParallelization that
// used to live here is removed). xUnit v2 runs collections concurrently (up to
// ProcessorCount) and runs DisableParallelization collections only AFTER every parallel
// collection finished, one at a time (verified at source, tag v2-2.7.0: the
// parallel/non-parallel partition is XunitTestAssemblyRunner.RunTestCollectionsAsync;
// the sequential classes-within-collection foreach is
// TestCollectionRunner.RunTestClassesAsync). The "Plugin" collection below is such an
// exclusive collection: every class touching Plugin.Instance (static singleton), QueueContinuationStore,
// RadioModeState, PlaybackReportOrdering, or the VideoAudioController encode-gate/registry
// statics MUST carry [Collection("Plugin")] and is thereby serialized against all of them
// and never overlaps the parallel phase. Classes WITHOUT a collection attribute must touch
// none of those statics (the JF-792 audit of all previously-uncollected classes; benign
// exceptions: PluginTempDirSweeper's ConcurrentBag registration, read-only IL/locale-file
// scans, self-contained per-class statics).

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// Resets all shared static state in the constructor.
/// Inherit from this class in every test class that references Plugin.Instance,
/// QueueContinuationStore, RadioModeState, or other static singletons, whether directly
/// or indirectly through BaseHandler methods (FilterByContentAccess, IfFeatureDisabled,
/// ApplyLibraryFilter). Since JF-792 (assembly parallelism ON) inheriting this class
/// ALSO requires carrying <c>[Collection("Plugin")]</c>: the per-test resets write the
/// shared statics, so an inheriting class outside the collection would race the
/// parallel phase.
///
/// This ensures each test class starts from a clean known-good state.
/// </summary>
public abstract class PluginTestBase
{
    protected PluginTestBase()
    {
        ResetSharedStatics();
    }

    /// <summary>
    /// The ONE reset sequence (JF-792 hoist: the second owner, VideoAudioControllerTests,
    /// takes the harness base instead of this class and must not carry a verbatim copy).
    /// Any future static that joins the per-test cleanup lands HERE only.
    /// </summary>
    internal static void ResetSharedStatics()
    {
        Plugin.ResetInstance();
        QueueContinuationStore.Clear();
        RadioModeState.Clear();
        // JF-447: the report-ordering guard keys its displacement classification on
        // static per-device state (the latest started item); without the reset, a
        // Started fired by one test would make a Stopped for a different item in a
        // LATER test classify as a displacement and skip the registration that later
        // test exercises.
        Jellyfin.Plugin.AlexaSkill.Alexa.Playback.PlaybackReportOrdering.Clear();
    }
}

/// <summary>
/// Test collection for all tests that create or depend on shared static state.
/// DisableParallelization (see the execution model at the top of this file) makes
/// this collection run exclusively AFTER every parallel collection, one class at
/// a time.
///
/// ALL test classes that reference Plugin.Instance, QueueContinuationStore,
/// RadioModeState, or other static singletons MUST be in this collection.
/// </summary>
[CollectionDefinition("Plugin", DisableParallelization = true)]
public class PluginCollection;

/// <summary>
/// The JF-792 gate-marker tail exemption for wall-clock-sensitive tests: classes whose
/// assertions carry real-time margins sized for a quiet machine (the RetryHelper
/// timeout-budget pin, DoubleMetaphone's encode-throughput margin, the JF-449
/// park-family's positive 2s waits). The parallel phase can starve a test continuation
/// for seconds (demonstrated 2026-10-06: a parked callback outlived its 5s bound), and
/// CI runners have 2-4 vCPU, so these classes run in a DisableParallelization
/// collection: exclusively after every parallel collection, one at a time, beside the
/// Plugin collection (a main-push red from a false-starved timing assert costs a
/// triage round; the exemption costs seconds of suite time). A test that needs the
/// Plugin collection's resets AND this exemption belongs in the Plugin collection
/// (it is already exclusive); this collection is for the static-free timing classes.
/// </summary>
[CollectionDefinition("TimingSolo", DisableParallelization = true)]
public class TimingSoloCollection;

/// <summary>
/// JF-432 structural assertion shared by every index service (extracted from the
/// near-verbatim per-service copies, JF-448 review F7): the published state must live
/// in ONE field typed as the immutable snapshot record, never in a group of separate
/// volatile fields. Volatile orders the individual assignments but not the group, so
/// sequential publishing let a reader observe a torn mix mid-refresh (new artist list
/// against the old top-parent map). Since JF-448 the field is owned by
/// <c>DebouncedLibraryIndexService{TSnapshot}</c>, so the walk covers the service and
/// its bases down to (excluding) the non-generic lifecycle base.
/// </summary>
public static class IndexSnapshotAssertions
{
    /// <summary>
    /// Asserts the single-snapshot-field invariant for an index service type.
    /// A future non-state instance field on the service or the generic base is fine
    /// ONLY if this helper's expectation is updated alongside it.
    /// </summary>
    /// <typeparam name="TService">The index service type.</typeparam>
    /// <typeparam name="TSnapshot">Its immutable snapshot record type.</typeparam>
    public static void AssertSingleSnapshotField<TService, TSnapshot>()
        where TService : DebouncedLibraryIndexService<TSnapshot>
        where TSnapshot : class
    {
        var declared = new List<FieldInfo>();
        for (Type? t = typeof(TService); t != null && t != typeof(DebouncedLibraryIndexService); t = t.BaseType)
        {
            declared.AddRange(
                t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly));
        }

        Assert.True(
            declared.Count == 1 && declared[0].FieldType == typeof(TSnapshot),
            $"{typeof(TService).Name} must declare exactly one published-state field of type {typeof(TSnapshot).Name} across itself and its snapshot-owning bases, found: {string.Join(", ", declared.Select(f => $"{f.FieldType.Name} {f.Name} (on {f.DeclaringType!.Name})"))}");
    }
}
