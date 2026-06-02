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
/// QueueContinuationStore, RadioModeState, or other static singletons
/// — whether directly or indirectly through BaseHandler methods (FilterByContentAccess,
/// IfFeatureDisabled, ApplyLibraryFilter).
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
