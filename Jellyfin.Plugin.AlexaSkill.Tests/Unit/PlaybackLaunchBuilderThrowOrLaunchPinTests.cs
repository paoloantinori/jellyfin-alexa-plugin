using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-732: the throw-or-launch contract of the AudioPlayer builder family,
/// pinned at the source (<see cref="PlaybackLaunchBuilder.EnsureLaunchResponse"/>;
/// the full contract, its load-bearing role for the ungated now-playing write
/// sites, and the removal policy live on that guard's doc, and the thinner-belt
/// decision on DeliveredLaunchStateWriteRosterTests' SCOPE paragraph). Two
/// halves here: the guard's SEMANTICS (directive-less, empty-list, and
/// non-launch-directive responses throw; both launch directive types pass and
/// the SAME instance is returned) and the WRAPS' PRESENCE (every family member
/// verifies its own outward return: a method that CONSTRUCTS a response must
/// call the pin, a pure delegation method must call another family member, so
/// the convenience overloads inherit and a new non-delegating overload without
/// a pin fails). RESIDUAL BOUNDARIES: a member with a pinned terminal return
/// could still grow an UNPINNED mid-body return (per-return IL analysis would
/// be needed to catch that shape), and the async wrapper's own pin is BELT
/// BEYOND this check (its member passes via the delegation lane, so deleting
/// that one wrap stays green; the three response-building members' wraps are
/// what the structural half enforces). SELF-RED: weaken the verdict and the
/// semantics tests flip; remove a response-building member's wrap or its
/// delegation and the structural test flips.
/// </summary>
public class PlaybackLaunchBuilderThrowOrLaunchPinTests
{
    [Fact]
    public void Pin_DirectivelessResponse_Throws()
    {
        var bare = new SkillResponse { Response = new ResponseBody() };
        Assert.Throws<InvalidOperationException>(() => PlaybackLaunchBuilder.EnsureLaunchResponse(bare));

        var emptyList = new SkillResponse { Response = new ResponseBody { Directives = new List<IDirective>() } };
        Assert.Throws<InvalidOperationException>(() => PlaybackLaunchBuilder.EnsureLaunchResponse(emptyList));
    }

    [Fact]
    public void Pin_NullResponseBody_ThrowsTheContractNotNre()
    {
        // The guard is null-total (code-review round): a contract-breaking return
        // with NO ResponseBody surfaces as the actionable contract throw, not a
        // NullReferenceException from inside the belt.
        var noBody = new SkillResponse();
        Assert.Throws<InvalidOperationException>(() => PlaybackLaunchBuilder.EnsureLaunchResponse(noBody));
    }

    [Fact]
    public void Pin_NonLaunchDirective_Throws()
    {
        // The verdict keys on the two LAUNCH directive types: a response carrying
        // only another directive (here a stop directive, the transport family) is
        // a non-launch return and must throw exactly like a bare response.
        var stopOnly = new SkillResponse
        {
            Response = new ResponseBody { Directives = new List<IDirective> { new StopDirective() } }
        };
        Assert.Throws<InvalidOperationException>(() => PlaybackLaunchBuilder.EnsureLaunchResponse(stopOnly));
    }

    [Fact]
    public void Pin_AudioPlayerLaunchDirective_PassesSameInstance()
    {
        var launch = new SkillResponse
        {
            Response = new ResponseBody { Directives = new List<IDirective> { new AudioPlayerPlayDirective() } }
        };
        Assert.Same(launch, PlaybackLaunchBuilder.EnsureLaunchResponse(launch));
    }

    [Fact]
    public void Pin_VideoAppLaunchDirective_PassesSameInstance()
    {
        var launch = new SkillResponse
        {
            Response = new ResponseBody { Directives = new List<IDirective> { new global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective() } }
        };
        Assert.Same(launch, PlaybackLaunchBuilder.EnsureLaunchResponse(launch));
    }

    [Fact]
    public void EveryFamilyMember_VerifiesItsOwnOutwardReturn()
    {
        Module module = typeof(PlaybackLaunchBuilder).Module;
        int pinToken = IlCallScanner.MethodTokens(
            typeof(PlaybackLaunchBuilder), nameof(PlaybackLaunchBuilder.EnsureLaunchResponse)).Single();

        string[] family =
        {
            nameof(PlaybackLaunchBuilder.BuildAudioPlayerResponse),
            nameof(PlaybackLaunchBuilder.BuildVideoAppAudioResponse),
            nameof(PlaybackLaunchBuilder.BuildAudiobookResumeResponse),
            nameof(PlaybackLaunchBuilder.BuildAudiobookVideoAppLaunchResponseAsync),
        };

        HashSet<int> familyTokens = new(family.SelectMany(
            member => IlCallScanner.MethodTokens(typeof(PlaybackLaunchBuilder), member)));

        // Group by logical name: a member covers its overloads, its lambdas/local
        // functions, and its async state machine (whose MoveNext maps back).
        var methodsByMember = new Dictionary<string, List<MethodBase>>();
        foreach ((Type type, MethodBase method) in IlCallScanner.DeclaredMethods(module.Assembly))
        {
            if (IlCallScanner.TopLevelType(method.DeclaringType ?? type) != typeof(PlaybackLaunchBuilder))
            {
                continue;
            }

            string logical = IlCallScanner.LogicalMethodName(method);
            if (!family.Contains(logical))
            {
                continue;
            }

            if (!methodsByMember.TryGetValue(logical, out List<MethodBase>? methods))
            {
                methods = new List<MethodBase>();
                methodsByMember[logical] = methods;
            }

            methods.Add(method);
        }

        var failures = new List<string>();
        foreach ((string member, List<MethodBase> methods) in methodsByMember)
        {
            // The async kickoff stub carries no logic (the body lives in the
            // MoveNext sibling, which IS lane-checked), so it is exempt; the
            // ReturnType keys the exemption to the stub itself (code-review
            // round), so a future SYNC overload of the same name cannot hide
            // behind an async sibling's state machine.
            bool hasStateMachine = methods.Any(m => m.Name == "MoveNext");
            foreach (MethodBase method in methods)
            {
                // A method that constructs its own response must pin the return; a
                // pure delegation method draws its value from another family member
                // (chains terminate at the master, whose terminal return is pinned).
                bool satisfies = IlCallScanner.ConstructsType(method, module, typeof(SkillResponse))
                    ? IlCallScanner.ContainsCallToToken(method, pinToken)
                    : IlCallScanner.ContainsCallToAnyToken(method, familyTokens);
                bool isAsyncKickoffStub = hasStateMachine
                    && method.DeclaringType == typeof(PlaybackLaunchBuilder)
                    && method.Name == member
                    && method is MethodInfo { ReturnType: var stubReturn }
                    && stubReturn == typeof(Task<SkillResponse>);
                if (!satisfies && !isAsyncKickoffStub)
                {
                    failures.Add($"{member} ({method.Name})");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"throw-or-launch family methods that neither pin their own return nor delegate to another family member: {string.Join(", ", failures)}");
    }
}
