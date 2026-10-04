using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-724 (item 1's structural half, the CaptureRefreshPairingTests IL-scan
/// discipline via the shared <see cref="IlCallScanner"/>): every read and
/// write of the LocaleModelStatuses collection must go through the locked
/// accessors on <see cref="PluginConfiguration"/> (Get/Set/Snapshot/Update).
/// No method OUTSIDE PluginConfiguration may touch the collection property
/// directly, because an unguarded enumeration throws (and 500s the admin
/// surface) when a concurrent writer's Add/indexer-set bumps the collection
/// version, and an unguarded read-then-write loses the intervening writer's
/// row (the KNOWN RACE class the accessors' single lock closes). The rule is
/// PLUGIN-CODE-only by construction: this scan covers the plugin assembly,
/// while the test assembly's own single-threaded seeding/clearing and
/// Jellyfin's configuration serializer (which enumerates the live collection;
/// every plugin-code save serializes against it via PersistUnderLedgerLock)
/// sit outside it. The scan is
/// loud-only: discovery walks every method body of the plugin assembly
/// (compiler-generated state machines and closures included), so a new direct
/// consumer always appears here and fails the roster; the accessor family
/// itself may touch the collection (compiler-generated shapes nested under
/// PluginConfiguration attribute to it via TopLevelType).
/// The SAVE side is machine-checked by the twin fact below (gate-marker
/// round 2 F1): a plugin-assembly call to SaveConfiguration is allowed ONLY
/// inside PersistUnderLedgerLock (the ONE locked save owner) and the two
/// Plugin.cs pre-startup migrations, so the "nothing else may call
/// SaveConfiguration directly" invariant fails the suite instead of resting
/// on prose.
/// </summary>
public class LocaleLedgerAccessorRosterTests
{
    [Fact]
    public void LocaleModelStatusesCollection_IsTouchedOnlyByItsOwningConfiguration()
    {
        Assembly pluginAssembly = typeof(PluginConfiguration).Assembly;
        Module module = typeof(PluginConfiguration).Module;
        MethodInfo getter = typeof(PluginConfiguration)
            .GetProperty(nameof(PluginConfiguration.LocaleModelStatuses))!
            .GetGetMethod(nonPublic: true)!;
        MethodInfo setter = typeof(PluginConfiguration)
            .GetProperty(nameof(PluginConfiguration.LocaleModelStatuses))!
            .GetSetMethod(nonPublic: true)!;

        var offenders = new SortedSet<string>();
        foreach (var (type, method) in IlCallScanner.DeclaredMethods(pluginAssembly))
        {
            bool reads = IlCallScanner.CallsNamedMethod(method, module, getter.Name, typeof(PluginConfiguration));
            bool writes = IlCallScanner.CallsNamedMethod(method, module, setter.Name, typeof(PluginConfiguration));
            if ((reads || writes) && IlCallScanner.TopLevelType(type) != typeof(PluginConfiguration))
            {
                offenders.Add($"{type.FullName}.{IlCallScanner.LogicalMethodName(method)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"LocaleModelStatuses must be read and written only through PluginConfiguration's locked accessors (GetLocaleModelStatus/SetLocaleModelStatus/GetLocaleModelStatusSnapshot/UpdateLocaleModelStatus, JF-724); direct touches: [{string.Join(", ", offenders)}]. " +
            "Route the new consumer through an accessor so a concurrent ledger writer cannot break its enumeration or clobber its row.");
    }

    /// <summary>
    /// The save-side twin (JF-724 gate-marker round 2 F1): every plugin-assembly
    /// call to the framework's SaveConfiguration must sit inside the ONE locked
    /// save owner (PluginConfiguration.PersistUnderLedgerLock) or one of the two
    /// Plugin.cs pre-startup migrations, because SaveConfiguration serializes
    /// the LIVE LocaleModelStatuses collection and an unlocked call throws
    /// inside the save when a ledger writer mutates concurrently (rows stranded
    /// memory-only, or the capture's return flipped). The scan matches by
    /// resolved callee NAME (SaveConfiguration is declared on the framework's
    /// generic BasePluginOfT base, whose token resolves as the constructed
    /// type): loud-only, so a coincidental same-named callee fails here and is
    /// added to the allowlist deliberately, never silently.
    /// </summary>
    [Fact]
    public void SaveConfiguration_IsCalledOnlyThroughTheLockedSaveOwnerOrPreStartupMigrations()
    {
        Assembly pluginAssembly = typeof(PluginConfiguration).Assembly;
        Module module = typeof(PluginConfiguration).Module;

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "PluginConfiguration.PersistUnderLedgerLock",
            "Plugin.MigrateDefaultInvocationNames",
            "Plugin.MigrateStaleCacheCapDefault",
        };

        var offenders = new SortedSet<string>();
        foreach (var (type, method) in IlCallScanner.DeclaredMethods(pluginAssembly))
        {
            foreach (int token in IlCallScanner.CallTokens(method))
            {
                if (IlCallScanner.TryResolveMethod(module, token) is not { } callee
                    || callee.Name != "SaveConfiguration")
                {
                    continue;
                }

                string site = $"{IlCallScanner.TopLevelType(type).Name}.{IlCallScanner.LogicalMethodName(method)}";
                if (!allowed.Contains(site))
                {
                    offenders.Add($"{type.FullName}.{IlCallScanner.LogicalMethodName(method)}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"SaveConfiguration must be called only through PluginConfiguration.PersistUnderLedgerLock (the ONE locked save owner) or the two Plugin.cs pre-startup migrations (JF-724); direct calls: [{string.Join(", ", offenders)}]. " +
            "Route the new save through PersistUnderLedgerLock so the serializer's enumeration of the live ledger cannot race a writer.");
    }
}
