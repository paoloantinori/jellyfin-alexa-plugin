using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-631: the AudioPlayer.Play construction-site roster as a TEST instead of
/// prose (the WarmingGateCoverageTests / SessionQueueReaderRosterTests
/// precedent). The invariant: every method that CONSTRUCTS an
/// <see cref="AudioPlayerPlayDirective"/> records the device ledger
/// (<see cref="DeviceQueueManager.RecordLastPlayed"/>) AND its launch base
/// (<see cref="DeviceQueueManager.RecordLaunchBase"/>). This exact
/// hand-assembly already produced two sequential misses at the sleep re-issue
/// (JF-522 forgot the base write, JF-628 forgot the ledger write), so the next
/// off-chokepoint construction site is a predicted repeat offender; this test
/// fails it by name instead of letting it surface as on-device
/// resume/classification desync. Ground truth is discovered by scanning the
/// plugin assembly's IL method bodies for newobj instructions on the
/// directive's constructor (the directive is an Alexa.NET type, so each token
/// is resolved through the module, the CallsGetter technique). VideoApp.Launch
/// paths that never mint an AudioPlayer.Play (the other RecordLastPlayed
/// callers in PlaybackLaunchBuilder and LastPlayedResponseInterceptor) are not
/// construction sites and never appear here. The IL walking lives in the shared
/// <see cref="IlCallScanner"/>.
/// </summary>
public class AudioPlayerPlayConstructionRosterTests
{
    /// <summary>
    /// A construction site: the top-level plugin type and the logical method
    /// name (async state machines and lambdas map back to the method that owns
    /// their body).
    /// </summary>
    private sealed record Site(Type Owner, string Method);

    /// <summary>
    /// The known-good construction sites:
    /// - PlaybackLaunchBuilder.BuildAudioPlayerResponse: the universal
    ///   chokepoint; its RecordLastPlayed (route Audio) sits BEFORE the
    ///   native-controls delegation and its RecordLaunchBase AFTER it (skipped
    ///   on the VideoApp delegation). The two writes are DELIBERATELY
    ///   non-adjacent and a combined helper was reviewed and REJECTED (JF-628);
    ///   do not "fix" the ordering.
    /// - SleepTimerIntentHandler.HandleAsync: the sleep re-issue/replay, the
    ///   one production site outside the chokepoint. Both constructions (the
    ///   cancel replay and the re-arm) live in this one method and share its
    ///   single guarded write block (JF-522 + JF-628).
    /// A NEW site here means a new AudioPlayer.Play launch exists: route it
    /// through the chokepoint, or add both writes at the site and extend this
    /// list WITH a justification comment after review (the second fact below
    /// still enforces the writes mechanically, whitelisted or not).
    /// </summary>
    private static readonly HashSet<Site> ExpectedSites = new()
    {
        new Site(typeof(PlaybackLaunchBuilder), nameof(PlaybackLaunchBuilder.BuildAudioPlayerResponse)),
        new Site(typeof(SleepTimerIntentHandler), nameof(SleepTimerIntentHandler.HandleAsync))
    };

    [Fact]
    public void PlayDirectiveConstructionRoster_MatchesAssemblyScan()
    {
        var discovered = ScanConstructionSites().Select(s => s.Site).ToHashSet();

        var listedButNotConstructing = new SortedSet<string>(
            ExpectedSites.Except(discovered).Select(Format));
        var constructingButNotListed = new SortedSet<string>(
            discovered.Except(ExpectedSites).Select(Format));

        Assert.True(
            listedButNotConstructing.Count == 0 && constructingButNotListed.Count == 0,
            "AudioPlayer.Play construction-site roster drifted from the assembly scan. " +
            (listedButNotConstructing.Count > 0
                ? $"Listed but no longer constructing AudioPlayerPlayDirective (renamed/removed?): [{string.Join(", ", listedButNotConstructing)}]. "
                : string.Empty) +
            (constructingButNotListed.Count > 0
                ? $"Constructs AudioPlayerPlayDirective but is NOT on the known-good roster: [{string.Join(", ", constructingButNotListed)}]. Every AudioPlayer.Play launch must record the device ledger (RecordLastPlayed) AND its launch base (RecordLaunchBase); this exact hand-assembly was missed twice at the sleep site (JF-522 base, JF-628 ledger). Route the launch through PlaybackLaunchBuilder.BuildAudioPlayerResponse, or add BOTH writes at the new site and extend ExpectedSites with a justification comment after review."
                : string.Empty));
    }

    [Fact]
    public void PlayDirectiveConstructionSites_RecordLedgerAndLaunchBase()
    {
        Module pluginModule = typeof(BaseHandler).Module;
        HashSet<int> ledgerTokens = WriteTokens(nameof(DeviceQueueManager.RecordLastPlayed));
        HashSet<int> baseTokens = WriteTokens(nameof(DeviceQueueManager.RecordLaunchBase));

        foreach ((MethodBase method, Site site) in ScanConstructionSites())
        {
            Assert.True(
                CallsDirectlyOrViaSameTypeHelper(method, pluginModule, ledgerTokens),
                $"{Format(site)} constructs an AudioPlayerPlayDirective but never calls DeviceQueueManager.RecordLastPlayed (directly or via a same-type helper). This is the JF-628 miss shape: the device ledger would not name the launched track, desyncing resume arbitration and medium classification. Add the write beside the construction (see PlaybackLaunchBuilder.BuildAudioPlayerResponse) or route through the chokepoint.");

            Assert.True(
                CallsDirectlyOrViaSameTypeHelper(method, pluginModule, baseTokens),
                $"{Format(site)} constructs an AudioPlayerPlayDirective but never calls DeviceQueueManager.RecordLaunchBase (directly or via a same-type helper). This is the JF-522 miss shape: a transcode-launched stream's stale base would compose over this directive's offsets at its playback events. Add the write beside the construction (see PlaybackLaunchBuilder.BuildAudioPlayerResponse) or route through the chokepoint.");
        }
    }

    /// <summary>
    /// Every method in the plugin assembly whose IL constructs the directive,
    /// with its site identity. Multiple constructions in one method body (the
    /// sleep handler's cancel + re-arm) yield one entry.
    /// </summary>
    private static List<(MethodBase Method, Site Site)> ScanConstructionSites()
    {
        Module pluginModule = typeof(BaseHandler).Module;
        var sites = new List<(MethodBase Method, Site Site)>();

        foreach (Type type in typeof(BaseHandler).Assembly.GetTypes())
        {
            foreach (MethodBase method in IlCallScanner.DeclaredCallableMethods(type))
            {
                if (IlCallScanner.ConstructsType(method, pluginModule, typeof(AudioPlayerPlayDirective)))
                {
                    Type owner = IlCallScanner.TopLevelType(method.DeclaringType ?? type);
                    sites.Add((method, new Site(owner, LogicalMethodName(method))));
                }
            }
        }

        return sites;
    }

    /// <summary>
    /// The metadata tokens of every <see cref="DeviceQueueManager"/> method with
    /// the given name (open-world over the write surface, the WarmingGate
    /// CoverageTests technique: a future overload cannot silently escape the
    /// check). Same-assembly methoddef tokens, compared verbatim by the scanner.
    /// </summary>
    private static HashSet<int> WriteTokens(string writeMethodName)
    {
        const BindingFlags allDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        var tokens = new HashSet<int>();
        foreach (MethodInfo m in typeof(DeviceQueueManager).GetMethods(allDeclared).Where(m => m.Name == writeMethodName))
        {
            tokens.Add(m.MetadataToken);
        }

        return tokens;
    }

    /// <summary>
    /// True when the method calls any target token directly, or calls (directly)
    /// a method declared under the SAME top-level type that calls it: the
    /// helper-extraction shape, so pulling the write out into a private helper
    /// of the same handler cannot detach the site from its write. MethodSpec
    /// (generic) tokens are skipped because they need type context to resolve;
    /// a skipped candidate can only fail this check loudly, never pass it
    /// silently.
    /// </summary>
    private static bool CallsDirectlyOrViaSameTypeHelper(MethodBase method, Module module, HashSet<int> targetTokens)
    {
        if (IlCallScanner.ContainsCallToAnyToken(method, targetTokens))
        {
            return true;
        }

        Type owner = IlCallScanner.TopLevelType(method.DeclaringType!);
        foreach (int token in IlCallScanner.CallTokens(method))
        {
            MethodBase? callee = IlCallScanner.TryResolveMethod(module, token);

            if (callee?.DeclaringType != null
                && IlCallScanner.TopLevelType(callee.DeclaringType) == owner
                && IlCallScanner.ContainsCallToAnyToken(callee, targetTokens))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The source-level method name an IL method belongs to: compiler-generated
    /// shapes map back to their owner (async state machine type
    /// &lt;Owner&gt;d__N.MoveNext, lambda &lt;Owner&gt;b__12_0, local function
    /// &lt;Owner&gt;g__Name|N); plain methods keep their own name.
    /// </summary>
    private static string LogicalMethodName(MethodBase method)
    {
        Type declared = method.DeclaringType!;
        string? owner = null;

        if (method.Name == "MoveNext" && declared.IsNested)
        {
            owner = ExtractCompilerGeneratedOwner(declared.Name);
        }

        owner ??= ExtractCompilerGeneratedOwner(method.Name);

        return owner ?? method.Name;
    }

    private static string? ExtractCompilerGeneratedOwner(string name)
    {
        if (name.Length == 0 || name[0] != '<')
        {
            return null;
        }

        int close = name.IndexOf('>');
        return close > 1 ? name.Substring(1, close - 1) : null;
    }

    private static string Format(Site site)
        => $"{site.Owner.Name}.{site.Method}";
}
