using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// Test collection for tests that create or mutate the static Plugin.Instance singleton.
/// Tests in this collection run sequentially (not in parallel with each other or
/// with tests outside the collection) to avoid race conditions on shared static state.
/// </summary>
[CollectionDefinition("Plugin", DisableParallelization = true)]
public class PluginCollection;
