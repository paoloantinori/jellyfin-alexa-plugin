using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;
using QueueContinuationStore = Jellyfin.Plugin.AlexaSkill.Alexa.QueueContinuationStore;
using RadioModeState = Jellyfin.Plugin.AlexaSkill.Alexa.RadioModeState;

// Prevent parallel test execution: handler tests share Plugin.Instance (static singleton),
// and BaseHandler static methods (FilterByContentAccess, IfFeatureDisabled, ApplyLibraryFilter)
// read Plugin.Instance?.Configuration. Parallel execution causes intermittent failures when
// one class toggles a feature flag and another class reads stale state concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// Resets all shared static state in the constructor.
/// Inherit from this class in every test class that references Plugin.Instance,
/// QueueContinuationStore, RadioModeState, or other static singletons, whether directly
/// or indirectly through BaseHandler methods (FilterByContentAccess, IfFeatureDisabled,
/// ApplyLibraryFilter).
///
/// This ensures each test class starts from a clean known-good state even though
/// tests run sequentially (not in parallel).
/// </summary>
public abstract class PluginTestBase
{
    protected PluginTestBase()
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
/// DisableParallelization ensures classes in this collection run sequentially,
/// complementing the assembly-level DisableTestParallelization.
///
/// ALL test classes that reference Plugin.Instance, QueueContinuationStore,
/// RadioModeState, or other static singletons MUST be in this collection.
/// </summary>
[CollectionDefinition("Plugin", DisableParallelization = true)]
public class PluginCollection;

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
