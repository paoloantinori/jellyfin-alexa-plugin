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
/// halves here: the guard's SEMANTICS (null response, directive-less,
/// empty-list, and non-launch-directive responses throw; both launch directive
/// types pass and the SAME instance is returned) and the WRAPS' PRESENCE in
/// three invariants: the DEEP construct rule (every builder method that
/// CONSTRUCTS a SkillResponse must call the pin unless it is one of the
/// documented Tell-capable VideoApp members, so a fifth or renamed
/// launch-building member cannot escape silently), STALENESS (every family and
/// exempt name must still name a declared method, so a rename that strands an
/// entry reds), and the FAMILY LANES (a family method that constructs pins, a
/// pure delegation method delegates to another family member, the async
/// kickoff stub exempt with its MoveNext checked). RESIDUAL BOUNDARIES: a
/// member with a pinned terminal return could still grow an UNPINNED mid-body
/// return (per-return IL analysis would be needed; that silent class is what
/// the rejected per-site roster arm uniquely covered), and the async wrapper's
/// own pin is BELT BEYOND the lanes (its member passes via delegation, so
/// deleting that one wrap stays green; the three response-building members'
/// wraps are what the construct rule enforces). SELF-RED: weaken the verdict
/// and the semantics tests flip; remove a response-building member's wrap, its
/// delegation, or its family entry and the structural test flips.
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
        // The guard is null-total (code-review round: the body; gate-marker round:
        // the response itself): a contract-breaking null response or a return with
        // NO ResponseBody surfaces as the actionable contract throw, not a
        // NullReferenceException from inside the belt.
        var noBody = new SkillResponse();
        Assert.Throws<InvalidOperationException>(() => PlaybackLaunchBuilder.EnsureLaunchResponse(noBody));

        Assert.Throws<InvalidOperationException>(() => PlaybackLaunchBuilder.EnsureLaunchResponse(null!));
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

        // The deliberate NON-pinned builders (the state roster's Tell-capable
        // VideoApp family: their capability/resolver Tells are their documented
        // contract, and their consumers are governed by
        // DeliveredLaunchStateWriteRosterTests). A constructing member in THIS
        // set is exempt from the deep construct rule below.
        string[] tellCapableExempt =
        {
            nameof(PlaybackLaunchBuilder.BuildVideoAppLaunchResponse),
            nameof(PlaybackLaunchBuilder.BuildVideoAppLaunchResponseAsync),
            nameof(PlaybackLaunchBuilder.BuildEpisodeLaunchResponseAsync),
            nameof(PlaybackLaunchBuilder.BuildChannelLaunchResponseAsync),
        };

        HashSet<int> familyTokens = new(family.SelectMany(
            member => IlCallScanner.MethodTokens(typeof(PlaybackLaunchBuilder), member)));

        // ONE walk collects the builder type's declared methods (its nested
        // closure included: async state machines, lambdas, local functions).
        var builderMethods = new List<MethodBase>();
        foreach ((Type type, MethodBase method) in IlCallScanner.DeclaredMethods(module.Assembly))
        {
            if (IlCallScanner.TopLevelType(method.DeclaringType ?? type) == typeof(PlaybackLaunchBuilder))
            {
                builderMethods.Add(method);
            }
        }

        // A compiler-generated shape that is NOT a state-machine MoveNext (a
        // formatting lambda, a display closure, a local function pulled out of a
        // member) is machinery, not a member: its return flows through the named
        // method that owns it, so the lanes below skip it (gate-marker F4).
        static bool IsLaneChecked(MethodBase method)
            => method.Name == "MoveNext" || (method.Name.Length > 0 && method.Name[0] != '<');

        var failures = new List<string>();

        // DEEP RULE (gate-marker F1): every method on the builder that CONSTRUCTS
        // a SkillResponse must call the pin unless it belongs to the Tell-capable
        // exempt set. Name-independent, so a FIFTH launch-building member or a
        // RENAMED one that constructs without a pin cannot escape silently, and a
        // rename of a Tell member strands its exempt entry into a loud red.
        foreach (MethodBase method in builderMethods)
        {
            if (!IsLaneChecked(method)
                || !IlCallScanner.ConstructsType(method, module, typeof(SkillResponse)))
            {
                continue;
            }

            string logical = IlCallScanner.LogicalMethodName(method);
            if (!tellCapableExempt.Contains(logical)
                && !IlCallScanner.ContainsCallToToken(method, pinToken))
            {
                failures.Add($"{logical} ({method.Name}) constructs a response with no pin");
            }
        }

        // STALENESS (gate-marker F1): a family name with no declared method means
        // a rename stranded the array entry and the renamed member escaped both
        // lanes above under an unknown name.
        foreach (string member in family.Concat(tellCapableExempt))
        {
            if (!builderMethods.Any(m => IlCallScanner.LogicalMethodName(m) == member))
            {
                failures.Add($"{member}: family/exempt name with no declared method (stale after a rename?)");
            }
        }

        // FAMILY LANES: every method of a known family member either pins its own
        // constructed return or delegates to another family member (the
        // convenience overloads), with the async kickoff stub exempt (its body
        // lives in the MoveNext sibling, which IS lane-checked; the ReturnType
        // keys the exemption to the stub itself so a future SYNC overload cannot
        // hide behind an async sibling's state machine).
        var methodsByMember = new Dictionary<string, List<MethodBase>>();
        foreach (MethodBase method in builderMethods)
        {
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

        foreach ((string member, List<MethodBase> methods) in methodsByMember)
        {
            bool hasStateMachine = methods.Any(m => m.Name == "MoveNext");
            foreach (MethodBase method in methods)
            {
                if (!IsLaneChecked(method))
                {
                    continue;
                }

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
            $"throw-or-launch pin coverage failures: {string.Join("; ", failures)}");
    }
}
