using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;

namespace Jellyfin.Plugin.AlexaSkill.Tests;

/// <summary>
/// JF-453: register-and-sweep for the GUID temp dirs test code mints for the
/// mocked <c>IApplicationPaths</c>. The old shape created a fresh
/// <c>&lt;suffix&gt;-&lt;guid&gt;</c> dir per first call and never deleted it; since xUnit
/// constructs every test class per test method and <see cref="PluginTestBase"/>'s
/// ctor resets <c>Plugin.Instance</c>, <c>TestHelpers.EnsurePluginInstance</c>
/// leaked one dir per test method (tens of thousands of empty dirs in /tmp at
/// filing). Owners register each dir right after creating it; a sweeper deletes
/// the registered set on <see cref="Dispose"/>.
/// </summary>
internal sealed class PluginTempDirSweeper : IDisposable
{
    private readonly ConcurrentBag<string> _dirs = new();

    /// <summary>
    /// Records a created temp dir for deletion on <see cref="Dispose"/>. Register
    /// only dirs this process created itself; never pattern-sweep /tmp, another
    /// concurrently running test process may own matching dirs.
    /// </summary>
    internal void Register(string dir) => _dirs.Add(dir);

    /// <summary>
    /// Deletes every registered dir, best effort: a dir that is already gone or
    /// locked is skipped, never thrown (same posture as DispatchHarness.Dispose).
    /// Draining the bag makes repeat calls no-ops.
    /// </summary>
    public void Dispose()
    {
        while (_dirs.TryTake(out string? dir))
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// JF-453: the ONE shared sweeper instance for this test assembly. xUnit 2.x has
/// no assembly fixture (a v3 feature), so the end-of-run sweep is hooked to
/// <see cref="AppDomain.ProcessExit"/> via a module initializer: every dir
/// registered during the run is deleted when the test host exits normally. A hard
/// kill (timeout -9) skips the sweep; that leaves at most one run's dirs, the same
/// bound the per-call leak already had. Test code that mints its own GUID temp
/// dirs registers them here instead of growing private cleanup copies.
/// </summary>
internal static class PluginTempDirCleanup
{
    internal static readonly PluginTempDirSweeper Shared = new();

    [ModuleInitializer]
    internal static void SweepOnProcessExit()
        => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shared.Dispose();
}
