using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-732 (code-review finding): the ONE scan core of the two delivered-launch
/// WRITE rosters (<see cref="DeliveredLaunchStateWriteRosterTests"/> for the
/// now-playing session fields, <see cref="DeliveredLaunchOutputSpeechRosterTests"/>
/// for OutputSpeech), hoisted at the second consumer by the IlCallScanner
/// JF-582/JF-634 precedent: the twins are the same loop and had already drifted
/// once (the state roster's depth-0 write probe vs the speech roster's
/// helper-deep one was the JF-718 gap this family had to close). The loop: a
/// method that calls a launch-builder token (directly or via a one-level
/// same-type helper), WRITES through the roster's probe (own IL or helper), and
/// references no gate token (directly or via helper) is a failure site. The
/// builder verdict runs first (pure token membership, no resolution; builder
/// callers are the minority), the helpers are DEDUPED (SameTypeHelpers yields
/// one entry per call occurrence), and the <see cref="PlaybackLaunchBuilder"/>
/// owner is skipped (its methods are the machinery the gates live in). The twins
/// keep their builder/gate name lists, allowlists, write probes, and failure
/// texts.
/// </summary>
internal static class DeliveredLaunchWriteRosterScan
{
    /// <param name="pluginModule">The plugin assembly's module (token resolution + the assembly walk).</param>
    /// <param name="builderTokens">The launch-builder family's methoddef tokens.</param>
    /// <param name="gateTokens">The accepted delivered-launch gate family's methoddef tokens.</param>
    /// <param name="writesProbe">The roster's write probe (own-IL leg; the helper leg is derived here).</param>
    /// <param name="allowlist">Sites allowed to write ungated, with their documented reasons (empty by policy in both twins).</param>
    /// <param name="remediation">The per-site failure text appended after the site name.</param>
    /// <returns>The failure sites, "Type.Method: remediation".</returns>
    internal static List<string> FindUngatedPostBuilderWrites(
        Module pluginModule,
        IReadOnlyCollection<int> builderTokens,
        IReadOnlyCollection<int> gateTokens,
        Func<MethodBase, Module, bool> writesProbe,
        IReadOnlyCollection<string> allowlist,
        string remediation)
    {
        var failures = new List<string>();
        foreach ((Type type, MethodBase method) in IlCallScanner.DeclaredMethods(pluginModule.Assembly))
        {
            Type owner = IlCallScanner.TopLevelType(method.DeclaringType ?? type);
            if (owner == typeof(PlaybackLaunchBuilder))
            {
                continue;
            }

            // ONE snapshot per method: the raw call tokens plus the deduped
            // same-type helpers, with the direct-or-helper token verdicts through
            // the shared CallsAnyDirectlyOrViaHelpers.
            int[] tokens = IlCallScanner.CallTokens(method).ToArray();
            MethodBase[] helpers = IlCallScanner.SameTypeHelpers(method, pluginModule).Distinct().ToArray();

            if (!IlCallScanner.CallsAnyDirectlyOrViaHelpers(tokens, helpers, builderTokens))
            {
                continue;
            }

            if (!writesProbe(method, pluginModule) && !helpers.Any(h => writesProbe(h, pluginModule)))
            {
                continue;
            }

            string site = $"{owner.Name}.{IlCallScanner.LogicalMethodName(method)}";
            if (IlCallScanner.CallsAnyDirectlyOrViaHelpers(tokens, helpers, gateTokens)
                || allowlist.Contains(site))
            {
                continue;
            }

            failures.Add($"{site}: {remediation}");
        }

        return failures;
    }
}
